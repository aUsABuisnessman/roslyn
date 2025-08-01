﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis;

internal sealed partial class SolutionCompilationState
{
    /// <summary>
    /// An implementation of <see cref="ICompilationTracker"/> that takes a compilation from another compilation tracker
    /// and updates it to return a generated document with a specific content, regardless of what the generator actually
    /// produces. In other words, it says "take the compilation this other thing produced, and pretend the generator
    /// gave this content, even if it wouldn't."  This is used by <see
    /// cref="Solution.WithFrozenSourceGeneratedDocuments"/> to ensure that a particular solution snapshot contains a
    /// pre-existing generated document from a prior run that the user is interacting with in the host.  The current
    /// snapshot might not produce the same content from before (or may not even produce that document anymore).  But we
    /// want to still let the user work with that doc effectively up until the point that new generated documents are
    /// produced and replace it in the host view.
    /// </summary>
    private sealed class WithFrozenSourceGeneratedDocumentsCompilationTracker : ICompilationTracker
    {
        private readonly TextDocumentStates<SourceGeneratedDocumentState> _replacementDocumentStates;

        /// <summary>
        /// The lazily-produced compilation that has the generated document updated. This is initialized by call to
        /// <see cref="GetCompilationAsync"/>.
        /// </summary>
        [DisallowNull]
        private Compilation? _compilationWithReplacements;

        public RegularCompilationTracker UnderlyingTracker { get; }
        public ProjectState ProjectState => UnderlyingTracker.ProjectState;

        public GeneratorDriver? GeneratorDriver => UnderlyingTracker.GeneratorDriver;

        /// <summary>
        /// Intentionally not readonly as this is a mutable struct.
        /// </summary>
        private SkeletonReferenceCache _skeletonReferenceCache;

        public WithFrozenSourceGeneratedDocumentsCompilationTracker(
            RegularCompilationTracker underlyingTracker,
            TextDocumentStates<SourceGeneratedDocumentState> replacementDocumentStates)
        {
            this.UnderlyingTracker = underlyingTracker;
            _replacementDocumentStates = replacementDocumentStates;
            _skeletonReferenceCache = underlyingTracker.GetClonedSkeletonReferenceCache();
        }

        public bool ContainsAssemblyOrModuleOrDynamic(
            ISymbol symbol, bool primary,
            [NotNullWhen(true)] out Compilation? compilation,
            out MetadataReferenceInfo? referencedThrough)
        {
            if (_compilationWithReplacements == null)
            {
                // We don't have a compilation yet, so this couldn't have came from us
                compilation = null;
                referencedThrough = null;
                return false;
            }

            return RootedSymbolSet.Create(_compilationWithReplacements).ContainsAssemblyOrModuleOrDynamic(
                symbol, primary, out compilation, out referencedThrough);
        }

        public ICompilationTracker Fork(ProjectState newProject, TranslationAction? translate)
        {
            // We'll apply the translation to the underlying tracker, and then replace the documents again.
            var underlyingTracker = this.UnderlyingTracker.Fork(newProject, translate);
            return new WithFrozenSourceGeneratedDocumentsCompilationTracker(underlyingTracker, _replacementDocumentStates);
        }

        public ICompilationTracker WithCreateCreationPolicy(bool forceRegeneration)
        {
            var underlyingTracker = this.UnderlyingTracker.WithCreateCreationPolicy(forceRegeneration);
            return underlyingTracker == this.UnderlyingTracker
                ? this
                : new WithFrozenSourceGeneratedDocumentsCompilationTracker(underlyingTracker, _replacementDocumentStates);
        }

        public ICompilationTracker WithDoNotCreateCreationPolicy()
        {
            var underlyingTracker = this.UnderlyingTracker.WithDoNotCreateCreationPolicy();
            return underlyingTracker == this.UnderlyingTracker
                ? this
                : new WithFrozenSourceGeneratedDocumentsCompilationTracker(underlyingTracker, _replacementDocumentStates);
        }

        /// <summary>
        /// Updates the frozen source generated documents states being tracked
        /// </summary>
        /// <remarks>
        /// NOTE: This does not merge the states currently tracked, it simply replaces them. If merging is desired, it should be done
        /// by the caller.
        /// </remarks>
        public ICompilationTracker WithReplacementDocumentStates(TextDocumentStates<SourceGeneratedDocumentState> replacementDocumentStates)
        {
            return new WithFrozenSourceGeneratedDocumentsCompilationTracker(this.UnderlyingTracker, replacementDocumentStates);
        }

        public async Task<Compilation> GetCompilationAsync(SolutionCompilationState compilationState, CancellationToken cancellationToken)
        {
            // Fast path if we've definitely already done this before
            if (_compilationWithReplacements != null)
            {
                return _compilationWithReplacements;
            }

            // We're building the real compilation for this tracker, so we want to include all generated docs at every
            // level of compilation tracker wrapping.  So pass along `withFrozenSourceGeneratedDocuments: true` to get a
            // full view of that.
            var underlyingSourceGeneratedDocuments = await UnderlyingTracker.GetSourceGeneratedDocumentStatesAsync(
                compilationState, withFrozenSourceGeneratedDocuments: true, cancellationToken).ConfigureAwait(false);
            var newCompilation = await UnderlyingTracker.GetCompilationAsync(compilationState, cancellationToken).ConfigureAwait(false);

            foreach (var (id, replacementState) in _replacementDocumentStates.States)
            {
                underlyingSourceGeneratedDocuments.TryGetState(id, out var existingState);

                var replacementSyntaxTree = await replacementState.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);

                if (existingState != null)
                {
                    // The generated file still exists in the underlying compilation, but the contents may not match the open file if the open file
                    // is stale. Replace the syntax tree so we have a tree that matches the text.
                    var existingSyntaxTree = await existingState.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
                    newCompilation = newCompilation.ReplaceSyntaxTree(existingSyntaxTree, replacementSyntaxTree);
                }
                else
                {
                    // The existing output no longer exists in the underlying compilation. This could happen if the user made
                    // an edit which would cause this file to no longer exist, but they're still operating on an open representation
                    // of that file. To ensure that this snapshot is still usable, we'll just add this document back in. This is not a
                    // semantically correct operation, but working on stale snapshots never has that guarantee.
                    newCompilation = newCompilation.AddSyntaxTrees(replacementSyntaxTree);
                }
            }

            Interlocked.CompareExchange(ref _compilationWithReplacements, newCompilation, null);

            return _compilationWithReplacements;
        }

        public Task<VersionStamp> GetDependentVersionAsync(SolutionCompilationState compilationState, CancellationToken cancellationToken)
            => UnderlyingTracker.GetDependentVersionAsync(compilationState, cancellationToken);

        public Task<VersionStamp> GetDependentSemanticVersionAsync(SolutionCompilationState compilationState, CancellationToken cancellationToken)
            => UnderlyingTracker.GetDependentSemanticVersionAsync(compilationState, cancellationToken);

        public async ValueTask<TextDocumentStates<SourceGeneratedDocumentState>> GetSourceGeneratedDocumentStatesAsync(
            SolutionCompilationState compilationState, bool withFrozenSourceGeneratedDocuments, CancellationToken cancellationToken)
        {
            var newStates = await UnderlyingTracker.GetSourceGeneratedDocumentStatesAsync(
                compilationState, withFrozenSourceGeneratedDocuments, cancellationToken).ConfigureAwait(false);

            // Only if the caller *wants* frozen source generated documents, then we will overlay the real underlying
            // generated docs with the frozen ones we're pointing at.
            if (withFrozenSourceGeneratedDocuments)
            {
                foreach (var (id, replacementState) in _replacementDocumentStates.States)
                {
                    if (newStates.Contains(id))
                    {
                        // The generated file still exists in the underlying compilation, but the contents may not match the open file if the open file
                        // is stale. Replace the syntax tree so we have a tree that matches the text.
                        newStates = newStates.SetState(replacementState);
                    }
                    else
                    {
                        // The generated output no longer exists in the underlying compilation. This could happen if the user made
                        // an edit which would cause this file to no longer exist, but they're still operating on an open representation
                        // of that file. To ensure that this snapshot is still usable, we'll just add this document back in. This is not a
                        // semantically correct operation, but working on stale snapshots never has that guarantee.
                        newStates = newStates.AddRange([replacementState]);
                    }
                }
            }

            return newStates;
        }

        public Task<bool> HasSuccessfullyLoadedAsync(
            SolutionCompilationState compilationState, CancellationToken cancellationToken)
        {
            return UnderlyingTracker.HasSuccessfullyLoadedAsync(compilationState, cancellationToken);
        }

        public bool TryGetCompilation([NotNullWhen(true)] out Compilation? compilation)
        {
            compilation = _compilationWithReplacements;
            return compilation != null;
        }

        public SourceGeneratedDocumentState? TryGetSourceGeneratedDocumentStateForAlreadyGeneratedId(DocumentId documentId)
        {
            if (_replacementDocumentStates.TryGetState(documentId, out var replacementState))
            {
                return replacementState;
            }
            else
            {
                return UnderlyingTracker.TryGetSourceGeneratedDocumentStateForAlreadyGeneratedId(documentId);
            }
        }

        public ValueTask<ImmutableArray<Diagnostic>> GetSourceGeneratorDiagnosticsAsync(
            SolutionCompilationState compilationState, CancellationToken cancellationToken)
        {
            // We can directly return the diagnostics from the underlying tracker; this is because
            // a generated document cannot have any diagnostics that are produced by a generator:
            // a generator cannot add diagnostics to it's own file outputs, and generators don't see the
            // outputs of each other.
            return UnderlyingTracker.GetSourceGeneratorDiagnosticsAsync(compilationState, cancellationToken);
        }

        public ValueTask<GeneratorDriverRunResult?> GetSourceGeneratorRunResultAsync(SolutionCompilationState solution, CancellationToken cancellationToken)
        {
            // The provided run result would be out of sync with the replaced documents.
            // Currently this is only used by razor to get the HostOutputs, which should never be used here.
            throw new NotImplementedException();
        }

        public SkeletonReferenceCache GetClonedSkeletonReferenceCache()
            => _skeletonReferenceCache.Clone();

        public Task<MetadataReference?> GetOrBuildSkeletonReferenceAsync(SolutionCompilationState compilationState, MetadataReferenceProperties properties, CancellationToken cancellationToken)
            => _skeletonReferenceCache.GetOrBuildReferenceAsync(this, compilationState, properties, cancellationToken);
    }
}
