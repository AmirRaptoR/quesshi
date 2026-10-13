using System.Text.RegularExpressions;

namespace Quesshi.Architecture.Tests;

public sealed class TimeSourceBoundaryTests
{
    [Fact]
    public void Production_code_uses_no_direct_wall_or_monotonic_time_source()
    {
        var root = PlatformDirectory.Find();
        var violations = TimeSourceBoundary.FindViolations(
            ProductionProjects.SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, "src", project), "*.cs", SearchOption.AllDirectories)));

        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("var now = DateTime.UtcNow;")]
    [InlineData("var now = DateTimeOffset.UtcNow;")]
    [InlineData("var timestamp = Stopwatch.GetTimestamp();")]
    public void Direct_time_sources_are_detected(string source)
    {
        var violations = TimeSourceBoundary.FindViolations([("fixture.cs", source)]);

        Assert.Single(violations);
    }

    private static readonly string[] ProductionProjects =
    [
        "Quesshi.Application",
        "Quesshi.Grains",
        "Quesshi.Features",
        "Quesshi.Formats",
        "Quesshi.Kinds"
    ];
}

internal static class TimeSourceBoundary
{
    private static readonly Regex ProhibitedTimeSource = new(
        @"\b(?:(?:global::)?System\.)?(?:DateTime|DateTimeOffset)\.UtcNow\b|\b(?:(?:global::)?System\.Diagnostics\.)?Stopwatch\b",
        RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> FindViolations(IEnumerable<string> paths) => FindViolations(paths.Select(path => (path, File.ReadAllText(path))));

    public static IReadOnlyList<string> FindViolations(IEnumerable<(string Path, string Source)> sources) => sources
        .SelectMany(source => ProhibitedTimeSource.Matches(source.Source).Select(match => $"{source.Path}: {match.Value}"))
        .ToArray();
}
