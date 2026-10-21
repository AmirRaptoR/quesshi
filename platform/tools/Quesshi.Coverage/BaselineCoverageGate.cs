using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Quesshi.Coverage;

public static partial class BaselineCoverageGate
{
    private const decimal AllowedRegression = 0.001m;
    private static readonly Regex ConditionCoveragePattern = ConditionCoverageRegex();

    public static BaselineCoverageResult Evaluate(string coveragePath, string? baselinePath, string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coveragePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        if (string.IsNullOrWhiteSpace(baselinePath))
        {
            return BaselineCoverageResult.SkippedResult;
        }

        var root = Path.GetFullPath(repositoryRoot);
        var currentLines = ReadCoverage(coveragePath, root);
        var baselineLines = ReadCoverage(baselinePath, root);
        var uncoveredLines = currentLines.Values.Where(line => line.Hits == 0).ToArray();
        var newlyUncoveredLines = uncoveredLines.Where(line => baselineLines.TryGetValue(line.Key, out var baseline) && baseline.Hits > 0).ToArray();
        var displayedLines = newlyUncoveredLines.Length > 0 ? newlyUncoveredLines : uncoveredLines;
        var partialBranches = currentLines.Values
            .Where(line => line.BranchCoverage is { } branch && branch.Covered < branch.Total)
            .Select(line => new PartialBranch(line.Key.Path, line.Key.LineNumber, line.BranchCoverage!.Value.Covered, line.BranchCoverage!.Value.Total))
            .OrderBy(branch => branch.Path, StringComparer.Ordinal)
            .ThenBy(branch => branch.LineNumber)
            .ToArray();

        return new BaselineCoverageResult(
            false,
            CoveragePercentage(currentLines.Values),
            CoveragePercentage(baselineLines.Values),
            displayedLines.Select(line => new ChangedLine(line.Key.Path, line.Key.LineNumber, ReadSourceLine(root, line.Key))).ToArray(),
            newlyUncoveredLines.Length > 0,
            partialBranches);
    }

    private static IReadOnlyDictionary<CoverageLineKey, CoverageLine> ReadCoverage(string coveragePath, string root)
    {
        return XDocument.Load(coveragePath)
            .Descendants("class")
            .SelectMany(@class => @class.Element("lines")?.Elements("line") ?? [], (@class, line) => new
            {
                Path = ToRepositoryPath(root, @class.Attribute("filename")?.Value ?? string.Empty),
                LineNumber = ParseInteger(line.Attribute("number")?.Value),
                Hits = ParseInteger(line.Attribute("hits")?.Value),
                BranchCoverage = ParseBranchCoverage(line)
            })
            .Where(line => line.LineNumber > 0 && IsProductionPath(line.Path))
            .GroupBy(line => new CoverageLineKey(line.Path, line.LineNumber))
            .ToDictionary(
                group => group.Key,
                group => new CoverageLine(group.Key, group.Sum(line => line.Hits), group.Select(line => line.BranchCoverage).FirstOrDefault(branch => branch is not null)));
    }

    private static decimal CoveragePercentage(IEnumerable<CoverageLine> lines)
    {
        var materialized = lines.ToArray();
        return materialized.Length == 0 ? 1m : materialized.Count(line => line.Hits > 0) / (decimal)materialized.Length;
    }

    private static bool IsProductionPath(string path)
    {
        if (!(path.StartsWith("platform/src/", StringComparison.Ordinal) || path.StartsWith("platform/tools/Quesshi.BankExport/", StringComparison.Ordinal)) || !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fileName = Path.GetFileName(path);
        return !path.Contains("/Migrations/", StringComparison.Ordinal)
            && !path.Contains("/obj/", StringComparison.Ordinal)
            && !fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            && !fileName.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
            && !fileName.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)
            && !fileName.EndsWith(".assemblyinfo.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadSourceLine(string root, CoverageLineKey line)
    {
        var path = Path.Combine(root, line.Path.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) && File.ReadLines(path).Skip(line.LineNumber - 1).FirstOrDefault() is { } source
            ? source
            : "<source unavailable>";
    }

    private static string ToRepositoryPath(string root, string path)
    {
        if (path.Length == 0)
        {
            return string.Empty;
        }

        var fullPath = Path.IsPathFullyQualified(path) ? path : Path.Combine(root, path);
        return Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
    }

    private static (int Covered, int Total)? ParseBranchCoverage(XElement line)
    {
        var match = ConditionCoveragePattern.Match(line.Attribute("condition-coverage")?.Value ?? string.Empty);
        return match.Success ? (ParseInteger(match.Groups[1].Value), ParseInteger(match.Groups[2].Value)) : null;
    }

    private static int ParseInteger(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    [GeneratedRegex(@"\((\d+)\/(\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex ConditionCoverageRegex();

    private sealed record CoverageLineKey(string Path, int LineNumber);

    private sealed record CoverageLine(CoverageLineKey Key, int Hits, (int Covered, int Total)? BranchCoverage);
}

public sealed record BaselineCoverageResult(
    bool Skipped,
    decimal CurrentCoverage,
    decimal BaselineCoverage,
    IReadOnlyList<ChangedLine> UncoveredLines,
    bool ListsNewlyUncoveredLines,
    IReadOnlyList<PartialBranch> PartialBranches)
{
    internal static BaselineCoverageResult SkippedResult { get; } = new(true, 0m, 0m, [], false, []);

    public bool Passed => Skipped || BaselineCoverage - CurrentCoverage <= 0.001m;

    public string FailureOutput()
    {
        if (Passed)
        {
            return string.Empty;
        }

        var output = new StringBuilder("Platform production coverage regressed.");
        output.AppendLine().Append(ListsNewlyUncoveredLines ? "Newly uncovered production lines:" : "Uncovered production lines:");
        foreach (var range in UncoveredRanges())
        {
            output.AppendLine().Append(range.Path).Append(':').Append(range.StartLine);
            if (range.StartLine != range.EndLine)
            {
                output.Append('-').Append(range.EndLine);
            }

            foreach (var line in range.Lines)
            {
                output.AppendLine().Append("  ").Append(line.LineNumber).Append(" | ").Append(line.SourceText);
            }
        }

        if (PartialBranches.Count > 0)
        {
            output.AppendLine().Append("Production branches with untested outcomes:");
            foreach (var branch in PartialBranches)
            {
                output.AppendLine().Append(branch.Path).Append(':').Append(branch.LineNumber).Append(": only ").Append(branch.CoveredOutcomes).Append(" of ").Append(branch.TotalOutcomes).Append(" outcomes tested");
            }
        }

        return output.AppendLine().Append("Test what this code does, or remove it if nothing needs it.").ToString();
    }

    private IEnumerable<UncoveredRange> UncoveredRanges()
    {
        UncoveredRange? range = null;
        foreach (var line in UncoveredLines.OrderBy(line => line.Path, StringComparer.Ordinal).ThenBy(line => line.LineNumber))
        {
            if (range is not null && range.Path == line.Path && range.EndLine + 1 == line.LineNumber)
            {
                range = range with { EndLine = line.LineNumber, Lines = [.. range.Lines, line] };
                continue;
            }

            if (range is not null)
            {
                yield return range;
            }

            range = new UncoveredRange(line.Path, line.LineNumber, line.LineNumber, [line]);
        }

        if (range is not null)
        {
            yield return range;
        }
    }

    private sealed record UncoveredRange(string Path, int StartLine, int EndLine, IReadOnlyList<ChangedLine> Lines);
}
