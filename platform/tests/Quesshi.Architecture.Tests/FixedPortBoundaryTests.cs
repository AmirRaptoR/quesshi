using System.Text.RegularExpressions;

namespace Quesshi.Architecture.Tests;

public sealed class FixedPortBoundaryTests
{
    [Fact]
    public void Test_and_benchmark_sources_bind_and_connect_to_no_hard_coded_port()
    {
        var root = PlatformDirectory.Find();
        var violations = FixedPortBoundary.FindViolations(
            new[] { "tests", "bench" }
                .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories))
                .Where(path => !IsBuildOutput(path) && !IsGuardSource(path)));

        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("app.Urls.Add(\"http://127.0.0.1:50145\");")]
    [InlineData("builder.WebHost.UseUrls(\"http://localhost:5000\");")]
    [InlineData("app.Urls.Add(\"http://*:8080\");")]
    [InlineData("builder.WebHost.UseUrls($\"http://127.0.0.1:{port}\");")]
    public void Http_server_bound_to_a_literal_port_is_detected(string source)
    {
        Assert.Single(FixedPortBoundary.FindViolations([("fixture.cs", source)]));
    }

    [Theory]
    [InlineData("using var client = new HttpClient { BaseAddress = new Uri(\"http://127.0.0.1:50147\") };")]
    [InlineData("var endpoint = new Uri(\"http://localhost:8123/health\");")]
    public void Client_connected_to_a_literal_loopback_port_is_detected(string source)
    {
        Assert.Single(FixedPortBoundary.FindViolations([("fixture.cs", source)]));
    }

    [Theory]
    [InlineData("Program.CreateApplication(sources, 11157, 30157);")]
    [InlineData("silo.ConfigureEndpoints(11111, 30000);")]
    [InlineData("silo.ConfigureEndpoints(IPAddress.Loopback, 11111, 30000, listenOnAnyHostAddress: false);")]
    public void Orleans_host_configured_with_a_literal_port_pair_is_detected(string source)
    {
        Assert.Single(FixedPortBoundary.FindViolations([("fixture.cs", source)]));
    }

    [Theory]
    [InlineData("app.Urls.Add(\"http://127.0.0.1:0\");")]
    [InlineData("var app = Program.CreateApplication(sources, TestPorts.Allocate(), TestPorts.Allocate());")]
    [InlineData("var postgres = Environment.GetEnvironmentVariable(\"QUESSHI_TEST_POSTGRES\");")]
    [InlineData("var mongo = Environment.GetEnvironmentVariable(\"QUESSHI_TEST_MONGO\") ?? string.Empty;")]
    [InlineData("var uri = new Uri(app.Urls.Single());")]
    public void Dynamic_ports_and_configured_external_services_are_accepted(string source)
    {
        Assert.Empty(FixedPortBoundary.FindViolations([("fixture.cs", source)]));
    }

    private static bool IsBuildOutput(string path) => path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin");

    // This file spells out the prohibited shapes as test data.
    private static bool IsGuardSource(string path) => Path.GetFileName(path) == nameof(FixedPortBoundaryTests) + ".cs";
}

internal static class FixedPortBoundary
{
    private static readonly Regex ServerUrl = new(
        @"(?:Urls\.Add|UseUrls)\(\s*\$?""[^""]*:(?!//)(?!0"")[^""]*""",
        RegexOptions.CultureInvariant);

    private static readonly Regex LoopbackClient = new(
        @"(?:127\.0\.0\.1|localhost|\[::1\]):[1-9]\d*",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex OrleansPorts = new(
        @"(?:ConfigureEndpoints|CreateApplication)\((?:[^;{}()]|\([^()]*\))*?\b[1-9]\d{2,5}\s*,\s*[1-9]\d{2,5}\b",
        RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> FindViolations(IEnumerable<string> paths) => FindViolations(paths.Select(path => (path, File.ReadAllText(path))));

    public static IReadOnlyList<string> FindViolations(IEnumerable<(string Path, string Source)> sources) => sources
        .SelectMany(source => FindViolations(source.Source).Select(match => $"{source.Path}: {match}"))
        .ToArray();

    private static IEnumerable<string> FindViolations(string source)
    {
        var server = ServerUrl.Matches(source).Select(match => match.Value).ToList();
        var client = LoopbackClient.Matches(source).Select(match => match.Value)
            .Where(value => !server.Any(bound => bound.Contains(value, StringComparison.Ordinal)));
        return server.Concat(client).Concat(OrleansPorts.Matches(source).Select(match => match.Value));
    }
}
