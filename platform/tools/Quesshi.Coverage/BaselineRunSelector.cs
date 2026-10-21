using System.Globalization;
using System.Text.Json;

namespace Quesshi.Coverage;

public static class BaselineRunSelector
{
    public static string? SelectNewestSuccessfulMainRun(string workflowRunsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowRunsPath);

        using var document = JsonDocument.Parse(File.ReadAllText(workflowRunsPath));
        if (!document.RootElement.TryGetProperty("workflow_runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Workflow runs response does not contain a workflow_runs array.");
        }

        return runs.EnumerateArray()
            .Select(ParseRun)
            .Where(run => run is not null)
            .Select(run => run!)
            .Where(run => run.Branch == "main" && run.Conclusion == "success")
            .OrderByDescending(run => run.CreatedAt)
            .ThenByDescending(run => run.Id, StringComparer.Ordinal)
            .Select(run => run.Id)
            .FirstOrDefault();
    }

    private static WorkflowRun? ParseRun(JsonElement run)
    {
        if (!run.TryGetProperty("id", out var id) || !run.TryGetProperty("head_branch", out var branch) || !run.TryGetProperty("conclusion", out var conclusion) || !run.TryGetProperty("created_at", out var createdAt)
            || id.ValueKind is not (JsonValueKind.Number or JsonValueKind.String)
            || branch.ValueKind != JsonValueKind.String
            || conclusion.ValueKind != JsonValueKind.String
            || createdAt.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(createdAt.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedCreatedAt))
        {
            return null;
        }

        return new WorkflowRun(id.ToString(), branch.GetString()!, conclusion.GetString()!, parsedCreatedAt);
    }

    private sealed record WorkflowRun(string Id, string Branch, string Conclusion, DateTimeOffset CreatedAt);
}
