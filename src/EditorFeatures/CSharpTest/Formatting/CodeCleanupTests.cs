﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.AddImport;
using Microsoft.CodeAnalysis.CodeCleanup;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeStyle;
using Microsoft.CodeAnalysis.CSharp.CodeStyle;
using Microsoft.CodeAnalysis.CSharp.UseExpressionBody;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Diagnostics.CSharp;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Editor.UnitTests;
using Microsoft.CodeAnalysis.Editor.UnitTests.CodeActions;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Test.Utilities;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.UnitTests.Diagnostics;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.Editor.CSharp.UnitTests.Formatting;

[UseExportProvider]
[Trait(Traits.Feature, Traits.Features.CodeCleanup)]
public sealed partial class CodeCleanupTests
{
    [Fact]
    public Task RemoveUsings()
    {
        return AssertCodeCleanupResult("""
            using System;
            internal class Program
            {
                private static void Main(string[] args)
                {
                    Console.WriteLine();
                }
            }
            """, """
            using System;
            using System.Collections.Generic;
            class Program
            {
                static void Main(string[] args)
                {
                    Console.WriteLine();
                }
            }
            """);
    }

    [Fact]
    public Task SortUsings()
    {
        return AssertCodeCleanupResult("""
            using System;
            using System.Collections.Generic;
            internal class Program
            {
                private static void Main(string[] args)
                {
                    List<int> list = new();
                    Console.WriteLine(list.Count);
                }
            }
            """, """
            using System.Collections.Generic;
            using System;
            class Program
            {
                static void Main(string[] args)
                {
                    var list = new List<int>();
                    Console.WriteLine(list.Count);
                }
            }
            """);
    }

    [Fact]
    public Task SortGlobalUsings()
    {
        return AssertCodeCleanupResult("""
            global using System;
            global using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            internal class Program
            {
                private static Task Main(string[] args)
                {
                    Barrier b = new(0);
                    List<int> list = new();
                    Console.WriteLine(list.Count);
                    b.Dispose();
                }
            }
            """, """
            using System.Threading.Tasks;
            using System.Threading;
            global using System.Collections.Generic;
            global using System;
            class Program
            {
                static Task Main(string[] args)
                {
                    Barrier b = new Barrier(0);
                    var list = new List<int>();
                    Console.WriteLine(list.Count);
                    b.Dispose();
                }
            }
            """);
    }

    [Fact, WorkItem("https://github.com/dotnet/roslyn/issues/36984")]
    public Task GroupUsings()
    {
        return AssertCodeCleanupResult("""
            using M;

            using System;

            internal class Program
            {
                private static void Main(string[] args)
                {
                    Console.WriteLine("Hello World!");

                    _ = new Goo();
                }
            }

            namespace M
            {
                public class Goo { }
            }
            """, """
            using M;
            using System;

            internal class Program
            {
                private static void Main(string[] args)
                {
                    Console.WriteLine("Hello World!");

                    new Goo();
                }
            }

            namespace M
            {
                public class Goo { }
            }
            """, systemUsingsFirst: false, separateUsingGroups: true);
    }

    [Fact, WorkItem("https://github.com/dotnet/roslyn/issues/36984")]
    public Task SortAndGroupUsings()
    {
        return AssertCodeCleanupResult("""
            using System;

            using M;

            internal class Program
            {
                private static void Main(string[] args)
                {
                    Console.WriteLine("Hello World!");

                    _ = new Goo();
                }
            }

            namespace M
            {
                public class Goo { }
            }
            """, """
            using M;
            using System;

            internal class Program
            {
                private static void Main(string[] args)
                {
                    Console.WriteLine("Hello World!");

                    new Goo();
                }
            }

            namespace M
            {
                public class Goo { }
            }
            """, systemUsingsFirst: true, separateUsingGroups: true);
    }

    [Fact]
    public Task FixAddRemoveBraces()
    {
        return AssertCodeCleanupResult("""
            internal class Program
            {
                private int Method()
                {
                    int a = 0;
                    if (a > 0)
                    {
                        a++;
                    }

                    return a;
                }
            }
            """, """
            class Program
            {
                int Method()
                {
                    int a = 0;
                    if (a > 0)
                        a ++;

                    return a;
                }
            }
            """);
    }

    [Fact]
    public Task RemoveUnusedVariable()
    {
        return AssertCodeCleanupResult("""
            internal class Program
            {
                private void Method()
                {
                }
            }
            """, """
            class Program
            {
                void Method()
                {
                    int a;
                }
            }
            """);
    }

    [Fact]
    public Task FixAccessibilityModifiers()
    {
        return AssertCodeCleanupResult("""
            internal class Program
            {
                private void Method()
                {
                }
            }
            """, """
            class Program
            {
                void Method()
                {
                    int a;
                }
            }
            """);
    }

    [Fact]
    public Task FixUsingPlacementPreferOutside()
    {
        return AssertCodeCleanupResult("""
            using System;

            namespace A
            {
                internal class Program
                {
                    private void Method()
                    {
                        Console.WriteLine();
                    }
                }
            }
            """, """
            namespace A
            {
                using System;

                internal class Program
                {
                    private void Method()
                    {
                        Console.WriteLine();
                    }
                }
            }
            """);
    }

    [Fact]
    public Task FixUsingPlacementPreferInside()
    {
        return AssertCodeCleanupResult("""
            namespace A
            {
                using System;

                internal class Program
                {
                    private void Method()
                    {
                        Console.WriteLine();
                    }
                }
            }
            """, """
            using System;

            namespace A
            {
                internal class Program
                {
                    private void Method()
                    {
                        Console.WriteLine();
                    }
                }
            }
            """, InsideNamespaceOption);
    }

    [Fact]
    public Task FixUsingPlacementPreferInsidePreserve()
    {
        var code = """
            using System;

            namespace A
            {
                internal class Program
                {
                    private void Method()
                    {
                        Console.WriteLine();
                    }
                }
            }
            """;

        var expected = code;

        return AssertCodeCleanupResult(expected, code, InsidePreferPreservationOption);
    }

    [Fact]
    public Task FixUsingPlacementPreferOutsidePreserve()
    {
        var code = """
            namespace A
            {
                using System;

                internal class Program
                {
                    private void Method()
                    {
                        Console.WriteLine();
                    }
                }
            }
            """;

        var expected = code;

        return AssertCodeCleanupResult(expected, code, OutsidePreferPreservationOption);
    }

    [Fact]
    public Task FixUsingPlacementMixedPreferOutside()
    {
        return AssertCodeCleanupResult("""
            using System;
            using System.Collections.Generic;

            namespace A
            {
                internal class Program
                {
                    private void Method()
                    {
                        Console.WriteLine();
                        List<int> list = [];
                        Console.WriteLine(list.Length);
                    }
                }
            }
            """, """
            using System;

            namespace A
            {
                using System.Collections.Generic;

                internal class Program
                {
                    private void Method()
                    {
                        Console.WriteLine();
                        List<int> list = new List<int>();
                        Console.WriteLine(list.Length);
                    }
                }
            }
            """, OutsideNamespaceOption);
    }

    [Fact]
    public Task FixUsingPlacementMixedPreferInside()
    {
        return AssertCodeCleanupResult("""
            namespace A
            {
                using System;
                using System.Collections.Generic;

                internal class Program
                {
                    private void Method()
                    {
                        Console.WriteLine();
                        List<int> list = [];
                        Console.WriteLine(list.Length);
                    }
                }
            }
            """, """
            using System;

            namespace A
            {
                using System.Collections.Generic;

                internal class Program
                {
                    private void Method()
                    {
                        Console.WriteLine();
                        List<int> list = new();
                        Console.WriteLine(list.Length);
                    }
                }
            }
            """, InsideNamespaceOption);
    }

    [Fact]
    public Task FixUsingPlacementMixedPreferInsidePreserve()
    {
        var code = """
            using System;

            namespace A
            {
                using System.Collections.Generic;

                internal class Program
                {
                    private void Method()
                    {
                        Console.WriteLine();
                        List<int> list = [];
                        Console.WriteLine(list.Length);
                    }
                }
            }
            """;

        var expected = code;

        return AssertCodeCleanupResult(expected, code, InsidePreferPreservationOption);
    }

    [Fact]
    public Task FixUsingPlacementMixedPreferOutsidePreserve()
    {
        var code = """
            using System;

            namespace A
            {
                using System.Collections.Generic;

                internal class Program
                {
                    private void Method()
                    {
                        Console.WriteLine();
                        List<int> list = [];
                        Console.WriteLine(list.Length);
                    }
                }
            }
            """;

        var expected = code;

        return AssertCodeCleanupResult(expected, code, OutsidePreferPreservationOption);
    }

    [Theory, WorkItem("https://github.com/dotnet/roslyn/issues/70187")]
    [CombinatorialData]
    public Task FixAllWarningsAndErrorsWithCustomFixIdsExplicitlyEnabled(
        bool applyAllAnalyzerFixersId,
        bool explicitlyIncludeCompilerId,
        [CombinatorialValues(DiagnosticSeverity.Warning, DiagnosticSeverity.Info)] DiagnosticSeverity severity)
    {
        var code = """
            namespace A
            {
                internal class Program
                {
                    private void Method()
                    {
                        int a = 42; // CS0219: The variable 'a' is assigned but its value is never used.
                    }
                }
            }
            """;

        var expectedCleanup = false;
        if (explicitlyIncludeCompilerId)
        {
            expectedCleanup = true;
        }
        else if (applyAllAnalyzerFixersId)
        {
            expectedCleanup = severity >= DiagnosticSeverity.Warning;
        }

        var expected = code;
        if (expectedCleanup)
        {
            expected = """
            namespace A
            {
                internal class Program
                {
                    private void Method()
                    {
                    }
                }
            }
            """;
        }

        Func<string, bool> enabledFixIdsFilter = id =>
            id switch
            {
                "CS0219" => explicitlyIncludeCompilerId,
                "ApplyAllAnalyzerFixersId" => applyAllAnalyzerFixersId,
                _ => false
            };

        var diagnosticIdsWithSeverity = new[] { ("CS0219", severity) };

        return AssertCodeCleanupResult(expected, code, enabledFixIdsFilter: enabledFixIdsFilter, diagnosticIdsWithSeverity: diagnosticIdsWithSeverity);
    }

    [Theory]
    [InlineData(LanguageNames.CSharp)]
    [InlineData(LanguageNames.VisualBasic)]
    public void VerifyAllCodeStyleFixersAreSupportedByCodeCleanup(string language)
    {
        var supportedDiagnostics = GetSupportedDiagnosticIdsForCodeCleanupService(language);

        // No Duplicates
        Assert.Equal(supportedDiagnostics, supportedDiagnostics.Distinct());
    }

    private const string _code = """
        class C
        {
            public void M1(int x, int y)
            {
                switch (x)
                {
                    case 1:
                    case 10:
                        break;
                    default:
                        break;
                }

                switch (y)
                {
                    case 1:
                        break;
                    case 1000:
                    default:
                        break;
                }

                switch (x)
                {
                    case 1:
                        break;
                    case 1000:
                        break;
                }

                switch (y)
                {
                    default:
                        break;
                }

                switch (y) { }

                switch (x)
                {
                    case :
                    case 1000:
                        break;
                }
            }
        }
        """;

    private const string _fixed = """
        class C
        {
            public void M1(int x, int y)
            {
                switch (x)
                {
                    case 1:
                    case 10:
                        break;
                }

                switch (y)
                {
                    case 1:
                        break;
                }

                switch (x)
                {
                    case 1:
                        break;
                    case 1000:
                        break;
                }

                switch (y)
                {
                }

                switch (y) { }

                switch (x)
                {
                    case :
                    case 1000:
                        break;
                }
            }
        }
        """;

    [Fact]
    public Task RunThirdPartyFixer()
        => TestThirdPartyCodeFixerApplied<TestThirdPartyCodeFixWithFixAll, CaseTestAnalyzer>(_code, _fixed);

    [Fact]
    public Task DoNotRunThirdPartyFixerWithNoFixAll()
        => TestThirdPartyCodeFixerNoChanges<TestThirdPartyCodeFixWithOutFixAll, CaseTestAnalyzer>(_code);

    [Theory]
    [InlineData(DiagnosticSeverity.Warning)]
    [InlineData(DiagnosticSeverity.Error)]
    public Task RunThirdPartyFixerWithSeverityOfWarningOrHigher(DiagnosticSeverity severity)
        => TestThirdPartyCodeFixerApplied<TestThirdPartyCodeFixWithFixAll, CaseTestAnalyzer>(_code, _fixed, severity);

    [Theory]
    [InlineData(DiagnosticSeverity.Hidden)]
    [InlineData(DiagnosticSeverity.Info)]
    public Task DoNotRunThirdPartyFixerWithSeverityLessThanWarning(DiagnosticSeverity severity)
        => TestThirdPartyCodeFixerNoChanges<TestThirdPartyCodeFixWithOutFixAll, CaseTestAnalyzer>(_code, severity);

    [Fact]
    public Task DoNotRunThirdPartyFixerIfItDoesNotSupportDocumentScope()
        => TestThirdPartyCodeFixerNoChanges<TestThirdPartyCodeFixDoesNotSupportDocumentScope, CaseTestAnalyzer>(_code);

    [Fact]
    public Task DoNotApplyFixerIfChangesAreMadeOutsideDocument()
        => TestThirdPartyCodeFixerNoChanges<TestThirdPartyCodeFixModifiesSolution, CaseTestAnalyzer>(_code);

    private static Task TestThirdPartyCodeFixerNoChanges<TCodefix, TAnalyzer>(string code, DiagnosticSeverity severity = DiagnosticSeverity.Warning)
        where TAnalyzer : DiagnosticAnalyzer, new()
        where TCodefix : CodeFixProvider, new()
    {
        return TestThirdPartyCodeFixer<TCodefix, TAnalyzer>(code, code, severity);
    }

    private static Task TestThirdPartyCodeFixerApplied<TCodefix, TAnalyzer>(string code, string expected, DiagnosticSeverity severity = DiagnosticSeverity.Warning)
        where TAnalyzer : DiagnosticAnalyzer, new()
        where TCodefix : CodeFixProvider, new()
    {
        return TestThirdPartyCodeFixer<TCodefix, TAnalyzer>(code, expected, severity);
    }

    private static async Task TestThirdPartyCodeFixer<TCodefix, TAnalyzer>(string code = null, string expected = null, DiagnosticSeverity severity = DiagnosticSeverity.Warning)
        where TAnalyzer : DiagnosticAnalyzer, new()
        where TCodefix : CodeFixProvider, new()
    {

        using var workspace = EditorTestWorkspace.CreateCSharp(code, composition: EditorTestCompositions.EditorFeatures.AddParts(typeof(TCodefix)));

        var project = workspace.CurrentSolution.Projects.Single();
        var analyzer = (DiagnosticAnalyzer)new TAnalyzer();
        var diagnosticIds = analyzer.SupportedDiagnostics.SelectAsArray(d => d.Id);

        var editorconfigText = "is_global = true";
        foreach (var diagnosticId in diagnosticIds)
        {
            editorconfigText += $"\ndotnet_diagnostic.{diagnosticId}.severity = {severity.ToEditorConfigString()}";
        }

        var map = new Dictionary<string, ImmutableArray<DiagnosticAnalyzer>>{
            { LanguageNames.CSharp, ImmutableArray.Create(analyzer) }
        };

        project = project.AddAnalyzerReference(new TestAnalyzerReferenceByLanguage(map));
        project = project.Solution.WithProjectFilePath(project.Id, @$"z:\\{project.FilePath}").GetProject(project.Id);
        project = project.AddAnalyzerConfigDocument(".editorconfig", SourceText.From(editorconfigText), filePath: @"z:\\.editorconfig").Project;
        workspace.TryApplyChanges(project.Solution);

        var hostdoc = workspace.Documents.Single();
        var document = workspace.CurrentSolution.GetDocument(hostdoc.Id);

        var codeCleanupService = document.GetLanguageService<ICodeCleanupService>();

        var enabledDiagnostics = codeCleanupService.GetAllDiagnostics();

        var newDoc = await codeCleanupService.CleanupAsync(
            document, enabledDiagnostics, CodeAnalysisProgress.None, CancellationToken.None);

        var actual = await newDoc.GetTextAsync();
        Assert.Equal(expected, actual.ToString());
    }

    private static string[] GetSupportedDiagnosticIdsForCodeCleanupService(string language)
    {
        using var workspace = GetTestWorkspaceForLanguage(language);
        var hostdoc = workspace.Documents.Single();
        var document = workspace.CurrentSolution.GetDocument(hostdoc.Id);

        var codeCleanupService = document.GetLanguageService<ICodeCleanupService>();

        var enabledDiagnostics = codeCleanupService.GetAllDiagnostics();
        var supportedDiagnostics = enabledDiagnostics.Diagnostics.SelectMany(x => x.DiagnosticIds).ToArray();
        return supportedDiagnostics;

        static EditorTestWorkspace GetTestWorkspaceForLanguage(string language)
        {
            if (language == LanguageNames.CSharp)
            {
                return EditorTestWorkspace.CreateCSharp(string.Empty, composition: EditorTestCompositions.EditorFeatures);
            }

            if (language == LanguageNames.VisualBasic)
            {
                return EditorTestWorkspace.CreateVisualBasic(string.Empty, composition: EditorTestCompositions.EditorFeatures);
            }

            return null;
        }
    }

    /// <summary>
    /// Assert the expected code value equals the actual processed input <paramref name="code"/>.
    /// </summary>
    /// <param name="expected">The actual processed code to verify against.</param>
    /// <param name="code">The input code to be processed and tested.</param>
    /// <param name="systemUsingsFirst">Indicates whether <c><see cref="System"/>.*</c> '<c>using</c>' directives should preceed others. Default is <c>true</c>.</param>
    /// <param name="separateUsingGroups">Indicates whether '<c>using</c>' directives should be organized into separated groups. Default is <c>true</c>.</param>
    /// <param name="enabledFixIdsFilter">Optional filter to determine if a specific fix ID is explicitly enabled for cleanup.</param>
    /// <param name="diagnosticIdsWithSeverity">Optional list of diagnostic IDs with effective severities to be configured in editorconfig.</param>
    /// <returns>The <see cref="Task"/> to test code cleanup.</returns>
    private static Task AssertCodeCleanupResult(string expected, string code, bool systemUsingsFirst = true, bool separateUsingGroups = false, Func<string, bool> enabledFixIdsFilter = null, (string, DiagnosticSeverity)[] diagnosticIdsWithSeverity = null)
        => AssertCodeCleanupResult(expected, code, new(AddImportPlacement.OutsideNamespace, NotificationOption2.Silent), systemUsingsFirst, separateUsingGroups, enabledFixIdsFilter, diagnosticIdsWithSeverity);

    /// <summary>
    /// Assert the expected code value equals the actual processed input <paramref name="code"/>.
    /// </summary>
    /// <param name="expected">The actual processed code to verify against.</param>
    /// <param name="code">The input code to be processed and tested.</param>
    /// <param name="preferredImportPlacement">Indicates the code style option for the preferred 'using' directives placement.</param>
    /// <param name="systemUsingsFirst">Indicates whether <c><see cref="System"/>.*</c> '<c>using</c>' directives should preceed others. Default is <c>true</c>.</param>
    /// <param name="separateUsingGroups">Indicates whether '<c>using</c>' directives should be organized into separated groups. Default is <c>true</c>.</param>
    /// <param name="enabledFixIdsFilter">Optional filter to determine if a specific fix ID is explicitly enabled for cleanup.</param>
    /// <param name="diagnosticIdsWithSeverity">Optional list of diagnostic IDs with effective severities to be configured in editorconfig.</param>
    /// <returns>The <see cref="Task"/> to test code cleanup.</returns>
    private static async Task AssertCodeCleanupResult(string expected, string code, CodeStyleOption2<AddImportPlacement> preferredImportPlacement, bool systemUsingsFirst = true, bool separateUsingGroups = false, Func<string, bool> enabledFixIdsFilter = null, (string, DiagnosticSeverity)[] diagnosticIdsWithSeverity = null)
    {
        using var workspace = EditorTestWorkspace.CreateCSharp(code, composition: EditorTestCompositions.EditorFeatures);

        // must set global options since incremental analyzer infra reads from global options
        workspace.SetAnalyzerFallbackAndGlobalOptions(new OptionsCollection(LanguageNames.CSharp)
        {
            { GenerationOptions.SeparateImportDirectiveGroups, separateUsingGroups },
            { GenerationOptions.PlaceSystemNamespaceFirst, systemUsingsFirst },
            { CSharpCodeStyleOptions.PreferredUsingDirectivePlacement, preferredImportPlacement },
        });

        var solution = workspace.CurrentSolution.WithAnalyzerReferences(
        [
            new AnalyzerFileReference(typeof(CSharpCompilerDiagnosticAnalyzer).Assembly.Location, TestAnalyzerAssemblyLoader.LoadFromFile),
            new AnalyzerFileReference(typeof(UseExpressionBodyDiagnosticAnalyzer).Assembly.Location, TestAnalyzerAssemblyLoader.LoadFromFile)
        ]);

        if (diagnosticIdsWithSeverity != null)
        {
            var editorconfigText = "is_global = true";
            foreach (var (diagnosticId, severity) in diagnosticIdsWithSeverity)
            {
                editorconfigText += $"\ndotnet_diagnostic.{diagnosticId}.severity = {severity.ToEditorConfigString()}";
            }

            var project = solution.Projects.Single();
            project = project.AddAnalyzerConfigDocument(".editorconfig", SourceText.From(editorconfigText), filePath: @"z:\\.editorconfig").Project;
            solution = project.Solution;
        }

        workspace.TryApplyChanges(solution);

        var hostdoc = workspace.Documents.Single();
        var document = workspace.CurrentSolution.GetDocument(hostdoc.Id);

        var codeCleanupService = document.GetLanguageService<ICodeCleanupService>();

        var enabledDiagnostics = codeCleanupService.GetAllDiagnostics();

        if (enabledFixIdsFilter != null)
            enabledDiagnostics = VisualStudio.LanguageServices.Implementation.CodeCleanup.AbstractCodeCleanUpFixer.AdjustDiagnosticOptions(enabledDiagnostics, enabledFixIdsFilter);

        var newDoc = await codeCleanupService.CleanupAsync(
            document, enabledDiagnostics, CodeAnalysisProgress.None, CancellationToken.None);

        var actual = await newDoc.GetTextAsync();

        Assert.Equal(expected, actual.ToString());
    }

    private static readonly CodeStyleOption2<AddImportPlacement> InsideNamespaceOption =
        new CodeStyleOption2<AddImportPlacement>(AddImportPlacement.InsideNamespace, NotificationOption2.Error);

    private static readonly CodeStyleOption2<AddImportPlacement> OutsideNamespaceOption =
        new CodeStyleOption2<AddImportPlacement>(AddImportPlacement.OutsideNamespace, NotificationOption2.Error);

    private static readonly CodeStyleOption2<AddImportPlacement> InsidePreferPreservationOption =
        new CodeStyleOption2<AddImportPlacement>(AddImportPlacement.InsideNamespace, NotificationOption2.None);

    private static readonly CodeStyleOption2<AddImportPlacement> OutsidePreferPreservationOption =
        new CodeStyleOption2<AddImportPlacement>(AddImportPlacement.OutsideNamespace, NotificationOption2.None);
}
