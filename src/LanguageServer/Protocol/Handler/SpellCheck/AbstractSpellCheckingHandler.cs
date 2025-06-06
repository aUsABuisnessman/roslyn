﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.LanguageServer.Handler.Diagnostics;
using Microsoft.CodeAnalysis.Serialization;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.SpellCheck;
using Microsoft.CommonLanguageServerProtocol.Framework;
using Roslyn.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler.SpellCheck;

/// <summary>
/// Root type for both document and workspace spell checking requests.
/// </summary>
internal abstract class AbstractSpellCheckHandler<TParams, TReport>
    : ILspServiceRequestHandler<TParams, TReport[]?>, ITextDocumentIdentifierHandler<TParams, TextDocumentIdentifier?>
    where TParams : IPartialResultParams<TReport[]>
    where TReport : VSInternalSpellCheckableRangeReport
{
    /// <summary>
    /// Cache where we store the data produced by prior requests so that they can be returned if nothing of
    /// significance changed. The version key is produced by combining the checksums for project options <see
    /// cref="ProjectState.GetParseOptionsChecksum"/> and <see cref="DocumentStateChecksums.Text"/>
    /// </summary>
    private readonly SpellCheckPullCache _versionedCache;

    public bool MutatesSolutionState => false;
    public bool RequiresLSPSolution => true;

    protected AbstractSpellCheckHandler()
    {
        _versionedCache = new(this.GetType().Name);
    }

    public abstract TextDocumentIdentifier? GetTextDocumentIdentifier(TParams requestParams);

    /// <summary>
    /// Retrieve the previous results we reported.  Used so we can avoid resending data for unchanged files. Also
    /// used so we can report which documents were removed and can have all their spell checking results cleared.
    /// </summary>
    protected abstract ImmutableArray<PreviousPullResult>? GetPreviousResults(TParams requestParams);

    /// <summary>
    /// Returns all the documents that should be processed in the desired order to process them in.
    /// </summary>
    protected abstract ImmutableArray<Document> GetOrderedDocuments(RequestContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the <see cref="VSInternalSpellCheckableRangeReport"/> instance we'll report back to clients to let them know our
    /// progress.  Subclasses can fill in data specific to their needs as appropriate.
    /// </summary>
    protected abstract TReport CreateReport(TextDocumentIdentifier identifier, int[]? ranges, string? resultId);

    public async Task<TReport[]?> HandleRequestAsync(
        TParams requestParams, RequestContext context, CancellationToken cancellationToken)
    {
        context.TraceDebug($"{this.GetType()} started getting spell checking spans");

        // The progress object we will stream reports to.
        using var progress = BufferedProgress.Create(requestParams.PartialResultToken);

        // Get the set of results the request said were previously reported.  We can use this to determine both
        // what to skip, and what files we have to tell the client have been removed.
        var previousResults = GetPreviousResults(requestParams) ?? [];
        context.TraceDebug($"previousResults.Length={previousResults.Length}");

        // First, let the client know if any workspace documents have gone away.  That way it can remove those for
        // the user from squiggles or error-list.
        await HandleRemovedDocumentsAsync(context, previousResults, progress, cancellationToken).ConfigureAwait(false);

        // Create a mapping from documents to the previous results the client says it has for them.  That way as we
        // process documents we know if we should tell the client it should stay the same, or we can tell it what
        // the updated spans are.
        var documentToPreviousParams = await GetDocumentToPreviousParamsAsync(context, previousResults, cancellationToken).ConfigureAwait(false);

        // Next process each file in priority order. Determine if spans are changed or unchanged since the
        // last time we notified the client.  Report back either to the client so they can update accordingly.
        var orderedDocuments = GetOrderedDocuments(context, cancellationToken);
        context.TraceDebug($"Processing {orderedDocuments.Length} documents");

        foreach (var document in orderedDocuments)
        {
            context.TraceDebug($"Processing: {document.FilePath}");

            var languageService = document.GetLanguageService<ISpellCheckSpanService>();
            if (languageService == null)
            {
                context.TraceDebug($"Ignoring document '{document.FilePath}' because it does not support spell checking");
                continue;
            }

            var documentToPreviousDiagnosticParams = documentToPreviousParams.ToDictionary(kvp => new ProjectOrDocumentId(kvp.Key.Id), kvp => kvp.Value);
            var newResult = await _versionedCache.GetOrComputeNewDataAsync(
                documentToPreviousDiagnosticParams,
                new ProjectOrDocumentId(document.Id),
                document.Project,
                new SpellCheckState(languageService, document),
                cancellationToken).ConfigureAwait(false);
            if (newResult != null)
            {
                var (newResultId, spans) = newResult.Value;
                context.TraceDebug($"Spans were changed for document: {document.FilePath}");
                foreach (var report in ReportCurrentSpans(
                    document, spans, newResultId))
                {
                    progress.Report(report);
                }
            }
            else
            {
                context.TraceDebug($"Spans were unchanged for document: {document.FilePath}");

                // Nothing changed between the last request and this one.  Report a (null-spans, same-result-id)
                // response to the client as that means they should just preserve the current spans they have for
                // this file.
                var previousParams = documentToPreviousParams[document];
                progress.Report(CreateReport(previousParams.TextDocument, ranges: null, previousParams.PreviousResultId));
            }
        }

        // If we had a progress object, then we will have been reporting to that.  Otherwise, take what we've been
        // collecting and return that.
        context.TraceDebug($"{this.GetType()} finished getting spans");
        return progress.GetFlattenedValues();
    }

    private static async Task<Dictionary<Document, PreviousPullResult>> GetDocumentToPreviousParamsAsync(
        RequestContext context, ImmutableArray<PreviousPullResult> previousResults, CancellationToken cancellationToken)
    {
        Contract.ThrowIfNull(context.Solution);

        var result = new Dictionary<Document, PreviousPullResult>();
        foreach (var requestParams in previousResults)
        {
            if (requestParams.TextDocument != null)
            {
                var document = await context.Solution.GetDocumentAsync(requestParams.TextDocument, cancellationToken).ConfigureAwait(false);
                if (document != null)
                    result[document] = requestParams;
            }
        }

        return result;
    }

    private IEnumerable<TReport> ReportCurrentSpans(
        Document document,
        ImmutableArray<SpellCheckSpan> spans,
        string resultId)
    {
        var textDocumentIdentifier = ProtocolConversions.DocumentToTextDocumentIdentifier(document);

        // protocol requires the results be in sorted order
        spans = spans.Sort(static (s1, s2) => s1.TextSpan.CompareTo(s2.TextSpan));

        if (spans.Length == 0)
        {
            yield return CreateReport(textDocumentIdentifier, [], resultId);
            yield break;
        }

        // break things up into batches of 1000 items.  That way we can send smaller messages to the client instead
        // of one enormous one.
        const int chunkSize = 1000;
        var lastSpanEnd = 0;
        for (var batchStart = 0; batchStart < spans.Length; batchStart += chunkSize)
        {
            var batchEnd = Math.Min(batchStart + chunkSize, spans.Length);
            var batchSize = batchEnd - batchStart;

            // Each span is encoded as a triple of ints.  The 'kind', the 'relative start', and the 'length'.
            // 'relative start' is the absolute-start for the first span, and then the offset from the end of the
            // last span for all others.
            var triples = new int[batchSize * 3];
            var triplesIndex = 0;
            for (var i = batchStart; i < batchEnd; i++)
            {
                var span = spans[i];

                var kind = ProtocolConversions.SpellCheckSpanKindToSpellCheckableRangeKind(span.Kind);

                triples[triplesIndex++] = (int)kind;
                triples[triplesIndex++] = span.TextSpan.Start - lastSpanEnd;
                triples[triplesIndex++] = span.TextSpan.Length;

                lastSpanEnd = span.TextSpan.End;
            }

            Contract.ThrowIfTrue(triplesIndex != triples.Length);
            yield return CreateReport(textDocumentIdentifier, triples, resultId);
        }
    }

    private async Task HandleRemovedDocumentsAsync(
        RequestContext context, ImmutableArray<PreviousPullResult> previousResults, BufferedProgress<TReport[]> progress, CancellationToken cancellationToken)
    {
        Contract.ThrowIfNull(context.Solution);

        foreach (var previousResult in previousResults)
        {
            var textDocument = previousResult.TextDocument;
            if (textDocument != null)
            {
                var document = await context.Solution.GetTextDocumentAsync(textDocument, cancellationToken).ConfigureAwait(false);
                if (document == null)
                {
                    context.TraceDebug($"Clearing spans for removed document: {textDocument.DocumentUri}");

                    // Client is asking server about a document that no longer exists (i.e. was removed/deleted from
                    // the workspace). Report a (null-spans, null-result-id) response to the client as that means
                    // they should just consider the file deleted and should remove all spans information they've
                    // cached for it.
                    progress.Report(CreateReport(textDocument, ranges: null, resultId: null));
                }
            }
        }
    }
}
