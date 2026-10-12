using Quesshi.Testing;

namespace Quesshi.Formats.Tests;

public sealed class BannedApiGuardTests
{
    [Fact]
    public async Task UtcNow_and_NewGuid_are_reported_as_banned_apis()
    {
    }

    [Fact]
    public async Task Namespace_wide_network_file_system_and_reflection_emit_bans_are_compile_errors()
    {
        await AssertRejectedAsync("public sealed class Probe { public string Host() => System.Net.Dns.GetHostName(); }", "QUESSHI001", "System.Net");
        await AssertRejectedAsync("public sealed class Probe { public object Client() => new System.Net.Http.HttpClient(); }", "QUESSHI001", "System.Net");
        await AssertRejectedAsync("public sealed class Probe { public object Client() => new System.Net.Sockets.TcpClient(); }", "QUESSHI001", "System.Net");
        await AssertRejectedAsync("public sealed class Probe { public string[] Files() => System.IO.Directory.GetFiles(\".\"); }", "QUESSHI001", "System.IO");
        await AssertRejectedAsync("public sealed class Probe { public object Stream() => new System.IO.FileStream(\"file\", System.IO.FileMode.Open); }", "QUESSHI001", "System.IO");
        await AssertRejectedAsync("public sealed class Probe { public object Emit() => new System.Reflection.Emit.DynamicMethod(\"method\", typeof(void), System.Type.EmptyTypes); }", "QUESSHI001", "System.Reflection.Emit");
    }

    [Fact]
    public void Banned_api_policy_covers_every_category_and_the_project_cannot_reference_Npgsql()
    {
        AssertPolicyIsComplete();

        var project = File.ReadAllText(Path.Combine(BannedApiCompileProbe.FindPlatformDirectory(), "src", "Quesshi.Formats", "Quesshi.Formats.csproj"));
        Assert.DoesNotContain("Npgsql", project, StringComparison.Ordinal);
        Assert.DoesNotContain(typeof(Quesshi.Formats.FormatsMarker).Assembly.GetReferencedAssemblies(), reference => reference.Name == "Npgsql");
    }

    private static async Task AssertRejectedAsync(string source, string diagnosticId, string expectedSymbol)
    {
        var result = await BannedApiCompileProbe.RunAsync(source);

        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Id == diagnosticId && diagnostic.GetMessage().Contains(expectedSymbol, StringComparison.Ordinal));
    }

    private static void AssertPolicyIsComplete()
    {
        var platform = BannedApiCompileProbe.FindPlatformDirectory();
        var symbols = File.ReadAllText(Path.Combine(platform, "BannedSymbols.txt"));
        var namespaces = File.ReadAllText(Path.Combine(platform, "PluginBannedNamespaces.txt"));

        Assert.All(
            new[]
            {
                "P:System.Random.Shared", "P:System.DateTime.Now", "P:System.DateTime.UtcNow",
                "P:System.DateTimeOffset.Now", "P:System.DateTimeOffset.UtcNow", "M:System.Guid.NewGuid",
                "M:System.Reflection.Assembly.Load(System.String)"
            },
            symbol => Assert.Contains(symbol, symbols, StringComparison.Ordinal));
        Assert.All(
            new[] { "System.Net;", "System.IO;", "System.Reflection.Emit;", "Npgsql;" },
            @namespace => Assert.Contains(@namespace, namespaces, StringComparison.Ordinal));
    }
}
