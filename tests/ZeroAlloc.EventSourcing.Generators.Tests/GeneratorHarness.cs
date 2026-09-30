using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ZeroAlloc.EventSourcing.Generators.Tests;

/// <summary>
/// Runs every ZeroAlloc.EventSourcing generator over an in-memory compilation built from
/// one or more source files, the way a consumer project would.
/// </summary>
internal static class GeneratorHarness
{
    public static GeneratorRun Run(params string[] sources)
    {
        var trees = sources.Select((s, i) => CSharpSyntaxTree.ParseText(s, path: $"File{i}.cs")).ToArray();

        var refs = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(System.IO.Path.PathSeparator)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
        refs.Add(MetadataReference.CreateFromFile(typeof(ZeroAlloc.EventSourcing.Aggregates.Aggregate<,>).Assembly.Location));
        refs.Add(MetadataReference.CreateFromFile(typeof(ZeroAlloc.EventSourcing.Projection<>).Assembly.Location));

        var compilation = CSharpCompilation.Create(
            "GeneratorTest_" + Guid.NewGuid().ToString("N"),
            trees,
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new AggregateDispatchGenerator(),
            new EventTypeRegistryGenerator(),
            new ProjectionDispatchGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);

        return new GeneratorRun(driver.GetRunResult(), generatorDiagnostics, output.GetDiagnostics(), [.. trees]);
    }
}

internal sealed record GeneratorRun(
    GeneratorDriverRunResult Result,
    ImmutableArray<Diagnostic> GeneratorDiagnostics,
    ImmutableArray<Diagnostic> CompilationDiagnostics,
    ImmutableArray<SyntaxTree> Inputs)
{
    /// <summary>The input source text a diagnostic points at.</summary>
    public string LocatedText(Diagnostic diagnostic)
    {
        var path = diagnostic.Location.GetLineSpan().Path;
        return Inputs.Single(t => t.FilePath == path).GetText().ToString(diagnostic.Location.SourceSpan);
    }

    public ImmutableArray<string> HintNames
        => Result.Results.SelectMany(r => r.GeneratedSources).Select(s => s.HintName).ToImmutableArray();

    public string Source(string hintName)
        => Result.Results.SelectMany(r => r.GeneratedSources).Single(s => s.HintName == hintName).SourceText.ToString();

    public ImmutableArray<Diagnostic> Errors
        => GeneratorDiagnostics.Concat(CompilationDiagnostics)
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();

    public ImmutableArray<Exception> Exceptions
        => Result.Results.Where(r => r.Exception is not null).Select(r => r.Exception!).ToImmutableArray();
}
