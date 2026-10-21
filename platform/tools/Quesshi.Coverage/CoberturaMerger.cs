using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Quesshi.Coverage;

public static partial class CoberturaMerger
{
    private static readonly Regex ConditionCoveragePattern = ConditionCoverageRegex();

    public static void Merge(IEnumerable<string> inputDirectories, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(inputDirectories);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var reports = inputDirectories
            .SelectMany(directory => Directory.EnumerateFiles(directory, "coverage.cobertura.xml", SearchOption.AllDirectories))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(XDocument.Load)
            .ToArray();
        if (reports.Length == 0)
        {
            throw new InvalidOperationException("No Cobertura reports were found to merge.");
        }

        var mergedClasses = reports
            .SelectMany(report => report.Descendants("class"))
            .Where(@class => @class.Attribute("filename") is not null)
            .GroupBy(@class => @class.Attribute("filename")!.Value, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(MergeClass)
            .ToArray();

        var lines = mergedClasses.SelectMany(@class => @class.Element("lines")!.Elements("line")).ToArray();
        var coveredLines = lines.Count(line => ParseInteger(line.Attribute("hits")?.Value) > 0);
        var branchCounts = lines.Select(ParseBranchCoverage).Where(counts => counts is not null).Select(counts => counts!.Value).ToArray();
        var coveredBranches = branchCounts.Sum(counts => counts.Covered);
        var totalBranches = branchCounts.Sum(counts => counts.Total);
        var coverage = new XElement("coverage",
            new XAttribute("line-rate", Rate(coveredLines, lines.Length)),
            new XAttribute("branch-rate", Rate(coveredBranches, totalBranches)),
            new XAttribute("version", "merged"),
            new XElement("packages", new XElement("package",
                new XAttribute("name", "merged"),
                new XAttribute("line-rate", Rate(coveredLines, lines.Length)),
                new XAttribute("branch-rate", Rate(coveredBranches, totalBranches)),
                new XAttribute("complexity", "0"),
                new XElement("classes", mergedClasses))));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        new XDocument(new XDeclaration("1.0", "utf-8", null), coverage).Save(outputPath);
    }

    private static XElement MergeClass(IGrouping<string, XElement> classes)
    {
        var lines = classes
            .SelectMany(@class => @class.Element("lines")?.Elements("line") ?? [])
            .GroupBy(line => ParseInteger(line.Attribute("number")?.Value))
            .OrderBy(group => group.Key)
            .Select(MergeLine)
            .ToArray();
        var coveredLines = lines.Count(line => ParseInteger(line.Attribute("hits")?.Value) > 0);
        var branchCounts = lines.Select(ParseBranchCoverage).Where(counts => counts is not null).Select(counts => counts!.Value).ToArray();
        var coveredBranches = branchCounts.Sum(counts => counts.Covered);
        var totalBranches = branchCounts.Sum(counts => counts.Total);
        return new XElement("class",
            new XAttribute("name", classes.Key),
            new XAttribute("filename", classes.Key),
            new XAttribute("line-rate", Rate(coveredLines, lines.Length)),
            new XAttribute("branch-rate", Rate(coveredBranches, totalBranches)),
            new XAttribute("complexity", "0"),
            new XElement("methods"),
            new XElement("lines", lines));
    }

    private static XElement MergeLine(IGrouping<int, XElement> lines)
    {
        var hits = lines.Sum(line => ParseInteger(line.Attribute("hits")?.Value));
        var branchCoverage = lines.Select(ParseBranchCoverage).Where(counts => counts is not null).Select(counts => counts!.Value).ToArray();
        (int Covered, int Total)? mergedBranchCoverage = branchCoverage.Length == 0
            ? null
            : (Covered: branchCoverage.Max(counts => counts.Covered), Total: branchCoverage.Max(counts => counts.Total));
        return new XElement("line",
            new XAttribute("number", lines.Key),
            new XAttribute("hits", hits.ToString(CultureInfo.InvariantCulture)),
            mergedBranchCoverage is null ? null : new XAttribute("branch", "True"),
            mergedBranchCoverage is null ? null : new XAttribute("condition-coverage", FormatConditionCoverage(mergedBranchCoverage.Value)));
    }

    private static (int Covered, int Total)? ParseBranchCoverage(XElement line)
    {
        var match = ConditionCoveragePattern.Match(line.Attribute("condition-coverage")?.Value ?? string.Empty);
        return match.Success
            ? (ParseInteger(match.Groups[1].Value), ParseInteger(match.Groups[2].Value))
            : null;
    }

    private static string FormatConditionCoverage((int Covered, int Total) counts) => $"{(counts.Total == 0 ? 100m : (decimal)counts.Covered / counts.Total * 100m).ToString("0.##", CultureInfo.InvariantCulture)}% ({counts.Covered}/{counts.Total})";

    private static string Rate(int covered, int total) => total == 0
        ? "1"
        : ((decimal)covered / total).ToString("0.########", CultureInfo.InvariantCulture);

    private static int ParseInteger(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    [GeneratedRegex(@"\((\d+)\/(\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex ConditionCoverageRegex();
}
