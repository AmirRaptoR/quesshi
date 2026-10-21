using System.Xml.Linq;
using Quesshi.Coverage;

namespace Quesshi.Coverage.Tests;

public sealed class ChangedLineCoverageGateTests
{
    [Fact]
    public void Cobertura_merge_sums_line_hits_from_both_test_runs()
    {
        using var fixture = new CoverageFixture();
        fixture.WriteCoverageTo("normal/coverage.cobertura.xml", ("platform/src/Quesshi.Domain/Score.cs", 1, 1));
        fixture.WriteCoverageTo("identity/coverage.cobertura.xml", ("platform/src/Quesshi.Domain/Score.cs", 1, 2));

        var output = Path.Combine(fixture.Root, "merged", "cobertura.xml");
        CoberturaMerger.Merge([Path.Combine(fixture.Root, "normal"), Path.Combine(fixture.Root, "identity")], output);

        Assert.Equal("3", XDocument.Load(output).Descendants("line").Single().Attribute("hits")?.Value);
    }

    [Fact]
    public void Eligible_changed_lines_include_only_coverable_production_sources()
    {
        using var fixture = new CoverageFixture();
        fixture.WriteSource("platform/src/Quesshi.Domain/Score.cs", "one", "two");
        fixture.WriteSource("platform/tools/Quesshi.BankExport/Export.cs", "one");
        fixture.WriteSource("platform/tests/Quesshi.Domain.Tests/ScoreTests.cs", "one");
        fixture.WriteSource("platform/src/Quesshi.Domain/Migrations/Seed.cs", "one");
        fixture.WriteSource("platform/src/Quesshi.Domain/Generated.g.cs", "one");
        fixture.WriteCoverage(
            ("platform/src/Quesshi.Domain/Score.cs", 1, 1),
            ("platform/src/Quesshi.Domain/Score.cs", 2, 1),
            ("platform/tools/Quesshi.BankExport/Export.cs", 1, 1),
            ("platform/tests/Quesshi.Domain.Tests/ScoreTests.cs", 1, 1),
            ("platform/src/Quesshi.Domain/Migrations/Seed.cs", 1, 1),
            ("platform/src/Quesshi.Domain/Generated.g.cs", 1, 1));
        fixture.WriteDiff(
            ("platform/src/Quesshi.Domain/Score.cs", 1),
            ("platform/src/Quesshi.Domain/Score.cs", 2),
            ("platform/tools/Quesshi.BankExport/Export.cs", 1),
            ("platform/tests/Quesshi.Domain.Tests/ScoreTests.cs", 1),
            ("platform/src/Quesshi.Domain/Migrations/Seed.cs", 1),
            ("platform/src/Quesshi.Domain/Generated.g.cs", 1));

        var result = ChangedLineCoverageGate.Evaluate(fixture.CoveragePath, fixture.DiffPath, fixture.Root);

        Assert.Equal(3, result.EligibleLineCount);
    }

    [Fact]
    public void Below_eighty_percent_changed_line_coverage_fails()
    {
        using var fixture = new CoverageFixture();
        fixture.WriteSource("platform/src/Quesshi.Domain/Score.cs", "one", "two", "three", "four", "five");
        fixture.WriteCoverage(
            ("platform/src/Quesshi.Domain/Score.cs", 1, 1, null),
            ("platform/src/Quesshi.Domain/Score.cs", 2, 1, null),
            ("platform/src/Quesshi.Domain/Score.cs", 3, 1, null),
            ("platform/src/Quesshi.Domain/Score.cs", 4, 0, null),
            ("platform/src/Quesshi.Domain/Score.cs", 5, 0, null));
        fixture.WriteDiff(("platform/src/Quesshi.Domain/Score.cs", 1), ("platform/src/Quesshi.Domain/Score.cs", 2), ("platform/src/Quesshi.Domain/Score.cs", 3), ("platform/src/Quesshi.Domain/Score.cs", 4), ("platform/src/Quesshi.Domain/Score.cs", 5));

        var result = ChangedLineCoverageGate.Evaluate(fixture.CoveragePath, fixture.DiffPath, fixture.Root);

        Assert.False(result.Passed);
    }

    [Fact]
    public void Eighty_percent_changed_line_coverage_passes_even_with_a_partial_branch()
    {
        using var fixture = new CoverageFixture();
        fixture.WriteSource("platform/src/Quesshi.Domain/Score.cs", "one", "two", "three", "four", "five");
        fixture.WriteCoverage(
            ("platform/src/Quesshi.Domain/Score.cs", 1, 1, null),
            ("platform/src/Quesshi.Domain/Score.cs", 2, 1, null),
            ("platform/src/Quesshi.Domain/Score.cs", 3, 1, null),
            ("platform/src/Quesshi.Domain/Score.cs", 4, 1, "50% (1/2)"),
            ("platform/src/Quesshi.Domain/Score.cs", 5, 0, null));
        fixture.WriteDiff(("platform/src/Quesshi.Domain/Score.cs", 1), ("platform/src/Quesshi.Domain/Score.cs", 2), ("platform/src/Quesshi.Domain/Score.cs", 3), ("platform/src/Quesshi.Domain/Score.cs", 4), ("platform/src/Quesshi.Domain/Score.cs", 5));

        var result = ChangedLineCoverageGate.Evaluate(fixture.CoveragePath, fixture.DiffPath, fixture.Root);

        Assert.True(result.Passed);
    }

    [Fact]
    public void A_diff_without_eligible_changed_lines_passes()
    {
        using var fixture = new CoverageFixture();
        fixture.WriteSource("platform/tests/Quesshi.Domain.Tests/ScoreTests.cs", "one");
        fixture.WriteCoverage(("platform/tests/Quesshi.Domain.Tests/ScoreTests.cs", 1, 0));
        fixture.WriteDiff(("platform/tests/Quesshi.Domain.Tests/ScoreTests.cs", 1));

        var result = ChangedLineCoverageGate.Evaluate(fixture.CoveragePath, fixture.DiffPath, fixture.Root);

        Assert.True(result.Passed);
    }

    [Fact]
    public void Failure_output_lists_uncovered_source_ranges_with_source_text()
    {
        using var fixture = new CoverageFixture();
        fixture.WriteSource("platform/src/Quesshi.Domain/Score.cs", "covered", "missing first", "missing second");
        fixture.WriteCoverage(("platform/src/Quesshi.Domain/Score.cs", 1, 1), ("platform/src/Quesshi.Domain/Score.cs", 2, 0), ("platform/src/Quesshi.Domain/Score.cs", 3, 0));
        fixture.WriteDiff(("platform/src/Quesshi.Domain/Score.cs", 1), ("platform/src/Quesshi.Domain/Score.cs", 2), ("platform/src/Quesshi.Domain/Score.cs", 3));

        var output = ChangedLineCoverageGate.Evaluate(fixture.CoveragePath, fixture.DiffPath, fixture.Root).FailureOutput();

        Assert.Contains("platform/src/Quesshi.Domain/Score.cs:2-3\n  2 | missing first\n  3 | missing second", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Failure_output_identifies_partially_tested_changed_branch_outcomes()
    {
        using var fixture = new CoverageFixture();
        fixture.WriteSource("platform/src/Quesshi.Domain/Score.cs", "branch", "missing");
        fixture.WriteCoverage(("platform/src/Quesshi.Domain/Score.cs", 1, 1, "50% (1/2)"), ("platform/src/Quesshi.Domain/Score.cs", 2, 0, null));
        fixture.WriteDiff(("platform/src/Quesshi.Domain/Score.cs", 1), ("platform/src/Quesshi.Domain/Score.cs", 2));

        var output = ChangedLineCoverageGate.Evaluate(fixture.CoveragePath, fixture.DiffPath, fixture.Root).FailureOutput();

        Assert.Contains("platform/src/Quesshi.Domain/Score.cs:1: only 1 of 2 outcomes tested", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Failure_output_ends_with_the_required_closing_sentence()
    {
        using var fixture = new CoverageFixture();
        fixture.WriteSource("platform/src/Quesshi.Domain/Score.cs", "missing");
        fixture.WriteCoverage(("platform/src/Quesshi.Domain/Score.cs", 1, 0));
        fixture.WriteDiff(("platform/src/Quesshi.Domain/Score.cs", 1));

        var output = ChangedLineCoverageGate.Evaluate(fixture.CoveragePath, fixture.DiffPath, fixture.Root).FailureOutput();

        Assert.EndsWith("Test what this code does, or remove it if nothing needs it.", output, StringComparison.Ordinal);
    }
}

file sealed class CoverageFixture : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "quesshi-coverage-tests", Guid.NewGuid().ToString("N"));

    public CoverageFixture()
    {
        Directory.CreateDirectory(root);
    }

    public string Root => root;
    public string CoveragePath => Path.Combine(root, "coverage.xml");
    public string DiffPath => Path.Combine(root, "changed.diff");

    public void WriteSource(string relativePath, params string[] lines)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
    }

    public void WriteCoverage(params (string Path, int Line, int Hits, string? ConditionCoverage)[] lines)
    {
        WriteCoverageTo("coverage.xml", lines);
    }

    public void WriteCoverage(params (string Path, int Line, int Hits)[] lines)
    {
        WriteCoverage(lines.Select(line => (line.Path, line.Line, line.Hits, (string?)null)).ToArray());
    }

    public void WriteCoverageTo(string relativePath, params (string Path, int Line, int Hits)[] lines)
    {
        WriteCoverageTo(relativePath, lines.Select(line => (line.Path, line.Line, line.Hits, (string?)null)).ToArray());
    }

    public void WriteCoverageTo(string relativePath, params (string Path, int Line, int Hits, string? ConditionCoverage)[] lines)
    {
        var classes = lines.GroupBy(line => line.Path, StringComparer.Ordinal)
            .Select(group => new XElement("class",
                new XAttribute("name", group.Key),
                new XAttribute("filename", group.Key),
                new XElement("lines", group.Select(line => new XElement("line",
                    new XAttribute("number", line.Line),
                    new XAttribute("hits", line.Hits),
                    line.ConditionCoverage is null ? null : new XAttribute("branch", "True"),
                    line.ConditionCoverage is null ? null : new XAttribute("condition-coverage", line.ConditionCoverage))))));
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        new XDocument(new XElement("coverage", new XElement("packages", new XElement("package", new XElement("classes", classes))))).Save(path);
    }

    public void WriteDiff(params (string Path, int Line)[] lines)
    {
        var sections = lines.GroupBy(line => line.Path, StringComparer.Ordinal).Select(group => string.Join("\n", new[]
        {
            $"diff --git a/{group.Key} b/{group.Key}",
            $"--- a/{group.Key}",
            $"+++ b/{group.Key}",
            $"@@ -0,0 +{group.Min(line => line.Line)},{group.Count()} @@"
        }.Concat(group.OrderBy(line => line.Line).Select(line => $"+{File.ReadLines(Path.Combine(root, line.Path)).ElementAt(line.Line - 1)}"))));
        File.WriteAllText(DiffPath, string.Join("\n", sections));
    }

    public void Dispose()
    {
        Directory.Delete(root, recursive: true);
    }
}
