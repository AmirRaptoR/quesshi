namespace Quesshi.Bench.Live;

/// <summary>The §7.4 budgets a live player count must meet.</summary>
public static class LiveBudgets
{
    public const double OrdinaryAnswerMs = 2;
    public const double RoundCloseMs = 50;
    public const double FinishMs = 100;
    /// <summary>The last accepted burst answer must be committed within this long after the question deadline.</summary>
    public const double BurstDrainMs = 2000;
    /// <summary>An accepted ordinary answer is acknowledged, durably committed, within this long of its acceptance.</summary>
    public const double AnswerCommitWaitMs = 2000;
}

/// <param name="Name">A stable identifier of the check.</param>
/// <param name="Applicable">False when the scenario cannot produce the measurement (for example, finish in a cold-activation run).</param>
/// <param name="Limit">The budget; observed values must be under it (zero for counted failures).</param>
public sealed record BudgetCheck(string Name, bool Applicable, double Limit, double? Observed, bool Passed, string Reason);

public sealed record BudgetAssessment(IReadOnlyList<BudgetCheck> Checks, bool Passed);

public sealed record CountResult(int Players, bool Passed, int Runs, IReadOnlyList<string> FailedChecks);

public sealed record LiveMaximumSelection(int? LiveMaximum, IReadOnlyList<CountResult> Counts, string Rationale);

public static class BudgetClassifier
{
    public static BudgetAssessment Assess(ScenarioReport report)
    {
        var finishes = report.Items == 1;
        var burst = report.Scenario != LiveScenario.Spread.CliName();
        var checks = new List<BudgetCheck>
        {
            Turn("ordinary_answer_max", true, LiveBudgets.OrdinaryAnswerMs, report.OrdinaryAnswerBusy),
            Turn("answer_commit_wait_max", true, LiveBudgets.AnswerCommitWaitMs, report.AnswerCommit),
            Turn("round_close_max", true, LiveBudgets.RoundCloseMs, report.RoundCloseBusy),
            Turn("finish_max", finishes, LiveBudgets.FinishMs, report.FinishBusy),
            Drain(burst, report),
            Count("on_time_refusals", report.OnTimeRefusedAtClose, "answers received by the deadline that were refused"),
            Count("receipt_coverage", report.CommandsWithoutServerReceipt, "commands with no server receipt (timeouts and transport failures)"),
        };
        return new BudgetAssessment(checks, checks.All(check => !check.Applicable || check.Passed));
    }

    // A turn budget is met only by a measured maximum under it: an unmeasured budget is not a pass.
    private static BudgetCheck Turn(string name, bool applicable, double limit, Distribution distribution) =>
        !applicable ? new(name, false, limit, null, true, "not produced by this scenario")
        : distribution.Max is not { } max ? new(name, true, limit, null, false, distribution.UnavailableReason ?? "no samples")
        : new(name, true, limit, max, max < limit, max < limit ? "within budget" : "over budget");

    private static BudgetCheck Drain(bool applicable, ScenarioReport report) =>
        !applicable ? new("burst_drain", false, LiveBudgets.BurstDrainMs, null, true, "answers are spread over the whole question")
        : report.LastAcceptedCommittedAfterDeadlineMs is not { } drain ? new("burst_drain", true, LiveBudgets.BurstDrainMs, null, false, "no answer was committed")
        : new("burst_drain", true, LiveBudgets.BurstDrainMs, drain, drain <= LiveBudgets.BurstDrainMs, drain <= LiveBudgets.BurstDrainMs ? "drained in time" : "drained after the grace");

    private static BudgetCheck Count(string name, int observed, string meaning) =>
        new(name, true, 0, observed, observed == 0, observed == 0 ? "none" : $"{observed} {meaning}");

    /// <summary>
    /// The live maximum is the largest tested count for which it and every smaller tested count met every applicable
    /// budget in every scenario and repetition. A larger count that passes after a smaller one failed is not selected.
    /// </summary>
    public static LiveMaximumSelection Select(IReadOnlyList<ScenarioReport> reports)
    {
        var counts = reports.GroupBy(report => report.Players).OrderBy(group => group.Key)
            .Select(group => new CountResult(
                group.Key,
                group.All(report => (report.Assessment ?? Assess(report)).Passed),
                group.Count(),
                group.SelectMany(report => (report.Assessment ?? Assess(report)).Checks
                        .Where(check => check.Applicable && !check.Passed)
                        .Select(check => $"{report.Scenario}:{check.Name}"))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()))
            .ToArray();
        int? maximum = null;
        foreach (var count in counts)
        {
            if (!count.Passed) break;
            maximum = count.Players;
        }
        var rationale = maximum is null
            ? "No tested count met every applicable budget, so no live maximum is established."
            : $"{maximum} is the largest tested count that, with every smaller tested count, met every applicable budget in every scenario."
              + (counts.Where(count => count.Players > maximum).ToArray() is { Length: > 0 } larger
                  ? " Larger tested counts were rejected: " + string.Join("; ", larger.Select(count => count.Passed
                      ? $"{count.Players} passed but follows a failing count"
                      : $"{count.Players} failed {string.Join(", ", count.FailedChecks)}"))
                  : "");
        return new LiveMaximumSelection(maximum, counts, rationale);
    }
}
