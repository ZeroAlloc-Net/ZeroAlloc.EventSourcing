using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using VerifyXunit;

namespace ZeroAlloc.EventSourcing.Mediator.Generator.Tests;

/// <summary>
/// Builds an in-memory CSharpCompilation, runs the EventSourcingMediatorGenerator,
/// and exposes both Verify snapshot helpers and raw diagnostic helpers.
/// </summary>
internal static class TestHarness
{
    // Synthetic IMediator stub. The published ZeroAlloc.Mediator marker DLL does NOT contain
    // IMediator (it is emitted by the Mediator generator into consumer compilations). The
    // bridge generator probes the compilation for IMediator before emitting, so we provide a
    // stub here that satisfies the probe and the emitted dispatcher's _mediator field.
    private const string MediatorStubSource = """
        namespace ZeroAlloc.Mediator
        {
            public interface IMediator
            {
                System.Threading.Tasks.ValueTask Publish<TNotification>(TNotification notification, System.Threading.CancellationToken ct)
                    where TNotification : INotification;
            }
        }
        """;

    public static Task Verify(string userSource)
    {
        var driver = RunDriver(userSource, out _);
        return Verifier.Verify(driver);
    }

    public static ImmutableArray<Diagnostic> RunDiagnostics(string userSource)
    {
        _ = RunDriver(userSource, out var diagnostics);
        return diagnostics;
    }

    private static GeneratorDriver RunDriver(string userSource, out ImmutableArray<Diagnostic> diagnostics)
    {
        var compilation = CreateCompilation(userSource);

        var generator = new EventSourcingMediatorGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out diagnostics);
        return driver;
    }

    private static CSharpCompilation CreateCompilation(string userSource)
    {
        var userTree = CSharpSyntaxTree.ParseText(userSource);
        var stubTree = CSharpSyntaxTree.ParseText(MediatorStubSource);

        // Trusted references — the runtime + Mediator marker package + the netstandard ref set.
        var trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(System.IO.Path.PathSeparator);

        var refs = trustedAssemblies
            .Select(p => MetadataReference.CreateFromFile(p))
            .Cast<MetadataReference>()
            .ToList();

        // Pull in our runtime + Mediator marker assemblies as loaded by this test process.
        refs.Add(MetadataReference.CreateFromFile(typeof(ZeroAlloc.Mediator.INotification).Assembly.Location));

        return CSharpCompilation.Create(
            assemblyName: "GeneratorSnapshotTest_" + Guid.NewGuid().ToString("N"),
            syntaxTrees: new[] { stubTree, userTree },
            references: refs,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }
}
