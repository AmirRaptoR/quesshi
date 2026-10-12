using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Quesshi.Testing;

public sealed record BannedApiCompileResult(ImmutableArray<Diagnostic> Diagnostics);

public static class BannedApiCompileProbe
{
    public static async Task<BannedApiCompileResult> RunAsync(string source)
    {
        var compilation = CSharpCompilation.Create(
            "Probe",
            [CSharpSyntaxTree.ParseText(source)],
            TrustedPlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var options = new AnalyzerOptions(
        [
            new FileAdditionalText(Path.Combine(FindPlatformDirectory(), "BannedSymbols.txt")),
            new FileAdditionalText(Path.Combine(FindPlatformDirectory(), "PluginBannedNamespaces.txt"))
        ]);
        var analyzers = LoadAnalyzers(PluginGuardAnalyzerPath()).ToImmutableArray();
        var diagnostics = await compilation.WithAnalyzers(
            analyzers,
            new CompilationWithAnalyzersOptions(options, null, concurrentAnalysis: false, logAnalyzerExecutionTime: false, reportSuppressedDiagnostics: false))
            .GetAnalyzerDiagnosticsAsync();

        return new BannedApiCompileResult(diagnostics);
    }

    public static string FindPlatformDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Quesshi.Platform.slnx"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not find the platform solution.");
    }

    private static IEnumerable<MetadataReference> TrustedPlatformReferences() =>
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? throw new InvalidOperationException("Trusted platform assemblies are unavailable."))
        .Split(Path.PathSeparator)
        .Select(path => MetadataReference.CreateFromFile(path));

    private static IEnumerable<DiagnosticAnalyzer> LoadAnalyzers(string path) =>
        Assembly.LoadFrom(path)
            .GetTypes()
            .Where(type => !type.IsAbstract && typeof(DiagnosticAnalyzer).IsAssignableFrom(type))
            .Select(type => (DiagnosticAnalyzer)Activator.CreateInstance(type)!);

    private static string PluginGuardAnalyzerPath()
    {
        var platformDirectory = FindPlatformDirectory();
        var configuredPath = AnalyzerPath(platformDirectory, BuildConfiguration());
        return File.Exists(configuredPath) ? configuredPath : AnalyzerPath(platformDirectory, "Debug");
    }

    private static string AnalyzerPath(string platformDirectory, string configuration) => Path.Combine(
        platformDirectory,
        "build",
        "Quesshi.PluginGuards",
        "bin",
        configuration,
        "netstandard2.0",
        "Quesshi.PluginGuards.dll");

    private static string BuildConfiguration() => new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";

    private sealed class FileAdditionalText(string path) : AdditionalText
    {
        public override string Path { get; } = path;

        public override SourceText? GetText(CancellationToken cancellationToken = default) => SourceText.From(File.ReadAllText(Path));
    }
}
