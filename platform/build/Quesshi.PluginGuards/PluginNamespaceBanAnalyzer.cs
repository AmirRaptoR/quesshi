using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Quesshi.PluginGuards;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PluginNamespaceBanAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "QUESSHI001";
    public const string BannedSymbolDiagnosticId = "QUESSHI002";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Plugin API is not available",
        "'{0}' is not available to pure plugins because it belongs to the banned namespace '{1}'",
        "Plugin isolation",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor BannedSymbolRule = new(
        BannedSymbolDiagnosticId,
        "Plugin API is not available",
        "'{0}' is not available to pure plugins because it is banned by the plugin API policy",
        "Plugin isolation",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule, BannedSymbolRule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(startContext =>
        {
            var bannedNamespaces = BannedNamespaces.Read(startContext.Options.AdditionalFiles);
            var bannedSymbols = BannedSymbols.Read(startContext.Options.AdditionalFiles);
            startContext.RegisterSyntaxNodeAction(
                syntaxContext => Analyze(syntaxContext, bannedNamespaces, bannedSymbols),
                SyntaxKind.IdentifierName,
                SyntaxKind.GenericName);
        });
    }

    private static void Analyze(
        SyntaxNodeAnalysisContext context,
        ImmutableArray<string> bannedNamespaces,
        ImmutableArray<string> bannedSymbols)
    {
        var symbol = context.SemanticModel.GetSymbolInfo(context.Node, context.CancellationToken).Symbol;
        if (symbol is null) return;

        var namespaceName = NamespaceName(symbol);
        var bannedNamespace = bannedNamespaces.FirstOrDefault(candidate =>
            namespaceName.Equals(candidate, StringComparison.Ordinal) ||
            namespaceName.StartsWith(candidate + ".", StringComparison.Ordinal));
        var displayName = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        var documentationId = symbol.GetDocumentationCommentId();
        var bannedSymbol = documentationId is not null && bannedSymbols.Contains(documentationId, StringComparer.Ordinal);
        if (!bannedSymbol && bannedNamespace is null) return;

        if (bannedSymbol)
        {
            context.ReportDiagnostic(Diagnostic.Create(BannedSymbolRule, context.Node.GetLocation(), displayName));
        }

        if (bannedNamespace is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, context.Node.GetLocation(), displayName, bannedNamespace));
        }
    }

    private static string NamespaceName(ISymbol symbol)
    {
        while (symbol is IAliasSymbol alias) symbol = alias.Target;
        return (symbol as INamespaceSymbol ?? symbol.ContainingNamespace).ToDisplayString();
    }
}

internal static class BannedNamespaces
{
    private const string PolicyFileName = "PluginBannedNamespaces.txt";

    public static ImmutableArray<string> Read(ImmutableArray<AdditionalText> files) =>
        files.FirstOrDefault(file => Path.GetFileName(file.Path).Equals(PolicyFileName, StringComparison.Ordinal))?.GetText()?.Lines
            .Select(line => line.ToString().Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal))
            .Select(line => line.Substring(0, line.IndexOf(';')).Trim())
            .ToImmutableArray() ?? [];
}

internal static class BannedSymbols
{
    private const string PolicyFileName = "BannedSymbols.txt";

    public static ImmutableArray<string> Read(ImmutableArray<AdditionalText> files) =>
        files.FirstOrDefault(file => Path.GetFileName(file.Path).Equals(PolicyFileName, StringComparison.Ordinal))?.GetText()?.Lines
            .Select(line => line.ToString().Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal))
            .Select(line => line.Substring(0, line.IndexOf(';')).Trim())
            .ToImmutableArray() ?? [];
}
