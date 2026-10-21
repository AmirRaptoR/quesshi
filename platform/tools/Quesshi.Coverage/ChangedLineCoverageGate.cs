using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Quesshi.Coverage;

public static partial class ChangedLineCoverageGate
{
    private static readonly Regex HunkPattern = HunkRegex();
    private static readonly Regex ConditionCoveragePattern = ConditionCoverageRegex();

    public static ChangedLineCoverageResult Evaluate(string coveragePath, string diffPath, string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coveragePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(diffPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        var root = Path.GetFullPath(repositoryRoot);
        var coveredLines = ReadCoverage(coveragePath, root);
        var changedLines = ReadChangedLines(diffPath)
            .Where(line => IsEligiblePath(line.Path))
            .Where(line => coveredLines.ContainsKey(line))
            .OrderBy(line => line.Path, StringComparer.Ordinal)
            .ThenBy(line => line.LineNumber)
            .Select(line => new ChangedLine(line.Path, line.LineNumber, ReadSourceLine(root, line)))
            .ToArray();
        var uncoveredLines = changedLines.Where(line => coveredLines[new CoverageLineKey(line.Path, line.LineNumber)].Hits == 0).ToArray();
        var partialBranches = changedLines
            .Select(line => (Line: line, Coverage: coveredLines[new CoverageLineKey(line.Path, line.LineNumber)]))
            .Where(entry => entry.Coverage.BranchCoverage is { } branch && branch.Covered < branch.Total)
            .Select(entry => new PartialBranch(entry.Line.Path, entry.Line.LineNumber, entry.Coverage.BranchCoverage!.Value.Covered, entry.Coverage.BranchCoverage!.Value.Total))
            .ToArray();
        return new ChangedLineCoverageResult(changedLines.Length, changedLines.Length - uncoveredLines.Length, uncoveredLines, partialBranches);
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
            .Where(line => line.LineNumber > 0 && line.Path.Length > 0)
            .GroupBy(line => new CoverageLineKey(line.Path, line.LineNumber))
            .ToDictionary(
                group => group.Key,
                group => new CoverageLine(group.Sum(line => line.Hits), group.Select(line => line.BranchCoverage).FirstOrDefault(branch => branch is not null)));
    }

    private static IEnumerable<CoverageLineKey> ReadChangedLines(string diffPath)
    {
        string? currentPath = null;
        var currentLine = 0;
        var inHunk = false;
        foreach (var line in File.ReadLines(diffPath))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                currentPath = null;
                inHunk = false;
                continue;
            }

            if (!inHunk && line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                currentPath = line[4..] == "/dev/null" ? null : NormalizeDiffPath(line[4..]);
                continue;
            }

            var hunk = HunkPattern.Match(line);
            if (hunk.Success)
            {
                currentLine = ParseInteger(hunk.Groups[1].Value);
                inHunk = true;
                continue;
            }

            if (currentPath is null || !inHunk)
            {
                continue;
            }

            if (line.StartsWith('+'))
            {
                yield return new CoverageLineKey(currentPath, currentLine++);
            }
            else if (!line.StartsWith('-') && !line.StartsWith('\\'))
            {
                currentLine++;
            }
        }
    }

    private static bool IsEligiblePath(string path)
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

    private static string NormalizeDiffPath(string path) => path.StartsWith("b/", StringComparison.Ordinal) ? path[2..] : path;

    private static (int Covered, int Total)? ParseBranchCoverage(XElement line)
    {
        var match = ConditionCoveragePattern.Match(line.Attribute("condition-coverage")?.Value ?? string.Empty);
        return match.Success ? (ParseInteger(match.Groups[1].Value), ParseInteger(match.Groups[2].Value)) : null;
    }

    private static int ParseInteger(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private sealed record CoverageLineKey(string Path, int LineNumber);

    private sealed record CoverageLine(int Hits, (int Covered, int Total)? BranchCoverage);

    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@", RegexOptions.CultureInvariant)]
    private static partial Regex HunkRegex();

    [GeneratedRegex(@"\((\d+)\/(\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex ConditionCoverageRegex();
}

public sealed record ChangedLine(string Path, int LineNumber, string SourceText);

public sealed record PartialBranch(string Path, int LineNumber, int CoveredOutcomes, int TotalOutcomes);

public sealed record ChangedLineCoverageResult(
    int EligibleLineCount,
    int CoveredLineCount,
    IReadOnlyList<ChangedLine> UncoveredLines,
    IReadOnlyList<PartialBranch> PartialBranches)
{
    public bool Passed => EligibleLineCount == 0 || CoveredLineCount / (decimal)EligibleLineCount >= 0.80m;

    public string FailureOutput()
    {
        if (Passed)
        {
            return string.Empty;
        }

        var output = new StringBuilder("Changed code contains untested eligible lines.");
        if (UncoveredLines.Count > 0)
        {
            output.AppendLine().Append("Uncovered changed lines:");
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
        }

        if (PartialBranches.Count > 0)
        {
            output.AppendLine().Append("Changed branches with untested outcomes:");
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
