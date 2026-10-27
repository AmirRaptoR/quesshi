using System.Diagnostics;
using System.Text.Json;
using Quesshi.Application.Sessions;

namespace Quesshi.Bench.Live.Tests;

public sealed class BudgetClassificationTests
{
    private static readonly DateTimeOffset Deadline = DateTimeOffset.UnixEpoch.AddSeconds(20);

    [Fact]
    public void A_scenario_inside_every_budget_passes_with_every_check_applicable()
    {
        var assessment = Report(LiveScenario.Burst, 100).Assessment!;

        Assert.True(assessment.Passed);
        Assert.Equal(
            ["ordinary_answer_max", "answer_commit_wait_max", "round_close_max", "finish_max", "burst_drain", "on_time_refusals", "receipt_coverage"],
            assessment.Checks.Select(check => check.Name));
        Assert.All(assessment.Checks, check => Assert.True(check.Applicable && check.Passed));
    }

    [Theory]
    [InlineData(2.0, 10, 10, 10, "ordinary_answer_max")]
    [InlineData(1.0, 50.0, 10, 10, "round_close_max")]
    [InlineData(1.0, 10, 100.0, 10, "finish_max")]
    [InlineData(1.0, 10, 10, 2000.5, "burst_drain")]
    public void A_turn_or_drain_at_its_limit_or_beyond_fails_only_that_check(double ordinary, double close, double finish, double drain, string failed)
    {
        var assessment = Report(LiveScenario.Burst, 100, ordinary, close, finish, drain).Assessment!;

        Assert.False(assessment.Passed);
        Assert.Equal([failed], assessment.Checks.Where(check => check.Applicable && !check.Passed).Select(check => check.Name));
    }

    [Theory]
    [InlineData(LiveScenario.Burst)]
    [InlineData(LiveScenario.Spread)]
    public void An_answer_commit_wait_at_its_limit_fails_in_every_scenario_including_spread(LiveScenario scenario)
    {
        var assessment = Report(scenario, 100, commitWait: 2000).Assessment!;

        Assert.Equal(["answer_commit_wait_max"], assessment.Checks.Where(check => check.Applicable && !check.Passed).Select(check => check.Name));
        Assert.True(Report(scenario, 100, commitWait: 1999).Assessment!.Passed);
        Assert.False(Report(scenario, 100, commitWait: null).Assessment!.Passed);
    }

    [Fact]
    public void Just_under_every_limit_passes_and_a_drain_exactly_at_the_grace_is_in_time()
    {
        Assert.True(Report(LiveScenario.Burst, 100, ordinary: 1.999, close: 49.999, finish: 99.999, drain: 2000).Assessment!.Passed);
    }

    [Fact]
    public void An_on_time_refusal_or_a_command_without_a_server_receipt_fails_the_scenario()
    {
        var refused = Report(LiveScenario.Burst, 100, onTimeRefusals: 1).Assessment!;
        Assert.Equal(["on_time_refusals"], refused.Checks.Where(check => check.Applicable && !check.Passed).Select(check => check.Name));

        var unreceipted = Report(LiveScenario.Burst, 100, withoutReceipt: 1).Assessment!;
        Assert.Equal(["receipt_coverage"], unreceipted.Checks.Where(check => check.Applicable && !check.Passed).Select(check => check.Name));
    }

    [Fact]
    public void Drain_is_not_applicable_to_spread_and_finish_is_not_applicable_to_cold_activation()
    {
        var spread = Report(LiveScenario.Spread, 100, drain: 5000).Assessment!;
        Assert.True(spread.Passed);
        Assert.False(spread.Checks.Single(check => check.Name == "burst_drain").Applicable);

        var cold = Report(LiveScenario.Cold, 100, finish: null).Assessment!;
        Assert.True(cold.Passed);
        Assert.False(cold.Checks.Single(check => check.Name == "finish_max").Applicable);
        Assert.True(cold.Checks.Single(check => check.Name == "burst_drain").Applicable);
    }

    [Fact]
    public void A_budget_that_was_not_measured_is_not_a_pass()
    {
        var noClose = Report(LiveScenario.Burst, 100, close: null).Assessment!;
        Assert.False(noClose.Passed);
        Assert.Contains(noClose.Checks, check => check.Name == "round_close_max" && !check.Passed && check.Observed is null);

        var noAnswersCommitted = Report(LiveScenario.Burst, 100, close: null, drain: null).Assessment!;
        Assert.Contains(noAnswersCommitted.Checks, check => check.Name == "burst_drain" && !check.Passed);
    }

    [Fact]
    public void The_live_maximum_is_the_largest_count_below_the_first_failure_and_names_every_rejected_count()
    {
        var selection = BudgetClassifier.Select(
        [
            .. Matrix(2), .. Matrix(20), .. Matrix(50),
            .. Matrix(100, ordinary: 7),
            .. Matrix(250), // passes again after 100 failed: isolated, not selected
            .. Matrix(500, onTimeRefusals: 3),
        ]);

        Assert.Equal(50, selection.LiveMaximum);
        Assert.Equal([2, 20, 50, 100, 250, 500], selection.Counts.Select(count => count.Players));
        Assert.Equal([true, true, true, false, true, false], selection.Counts.Select(count => count.Passed));
        Assert.Contains("burst:ordinary_answer_max", selection.Counts.Single(count => count.Players == 100).FailedChecks);
        Assert.Contains("100 failed", selection.Rationale, StringComparison.Ordinal);
        Assert.Contains("250 passed but follows a failing count", selection.Rationale, StringComparison.Ordinal);
        Assert.Contains("500 failed", selection.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public void A_count_passes_only_when_every_scenario_and_repetition_passes()
    {
        ScenarioReport[] reports = [.. Matrix(2), .. Matrix(20)];
        var oneBadRepetition = reports.Select(report =>
            report.Players == 20 && report.Scenario == "pool10" ? Report(LiveScenario.Pool10, 20, ordinary: 3) : report).ToArray();

        var selection = BudgetClassifier.Select(oneBadRepetition);

        Assert.Equal(2, selection.LiveMaximum);
        Assert.Equal(["pool10:ordinary_answer_max"], selection.Counts.Single(count => count.Players == 20).FailedChecks);
    }

    [Fact]
    public void No_passing_count_establishes_no_live_maximum()
    {
        var selection = BudgetClassifier.Select([.. Matrix(2, close: 80), .. Matrix(20, close: 80)]);

        Assert.Null(selection.LiveMaximum);
        Assert.Contains("no live maximum", selection.Rationale, StringComparison.Ordinal);
        Assert.All(selection.Counts, count => Assert.False(count.Passed));
    }

    [Fact]
    public void The_checked_in_report_has_the_current_schema_and_a_selection_that_its_own_data_reproduces()
    {
        var report = JsonSerializer.Deserialize<BenchmarkReport>(File.ReadAllText(RepositoryFile("docs/platform/benchmark.raw.json")))!;

        Assert.Equal(BenchmarkReport.CurrentSchema, report.Schema);
        Assert.Matches("^[0-9a-f]{40}$", report.SourceRevision);
        // The measured revision is a commit of the pull request that adds the report. That pull
        // request's CI has the commit and checks it here; a squash merge then removes it from
        // main's history, so a revision this repository no longer has is accepted.
        Assert.True(
            !GitHasCommit(report.SourceRevision) || IsAncestorOfHead(report.SourceRevision),
            $"The report source revision {report.SourceRevision} is not an ancestor of HEAD.");
        Assert.NotEmpty(report.Command);
        Assert.Equal("anonymized", report.Environment.HostClass);
        Assert.DoesNotContain("\"Hostname\"", JsonSerializer.Serialize(report.Environment), StringComparison.Ordinal);
        Assert.Equal("local PostgreSQL", report.Environment.PostgreSqlLocation);
        Assert.False(string.IsNullOrWhiteSpace(report.Environment.PostgreSqlVersion));

        var counts = report.Scenarios.Select(scenario => scenario.Players).Distinct().Order().ToArray();
        foreach (var required in new[] { 2, 20, 100, 250, 500 }) Assert.Contains(required, counts);
        foreach (var count in counts)
            Assert.Equal(
                Enum.GetValues<LiveScenario>().Select(scenario => scenario.CliName()).Order(),
                report.Scenarios.Where(scenario => scenario.Players == count).Select(scenario => scenario.Scenario).Distinct().Order());

        foreach (var scenario in report.Scenarios)
        {
            var label = $"{scenario.Scenario}/{scenario.Players}/{scenario.Repetition}";
            Assert.True(scenario.CommandsSent == scenario.Players, label);
            Assert.True(scenario.OrdinaryAnswerBusy.SampleCount > 0 || scenario.OrdinaryAnswerBusy.UnavailableReason is not null, label);
            Assert.True(scenario.RoundCloseBusy.SampleCount > 0 || scenario.RoundCloseBusy.UnavailableReason is not null, label);
            Assert.NotNull(scenario.Gc);
            Assert.True(scenario.CommandsWithoutServerReceipt >= 0, label);
            Assert.NotNull(scenario.FailureCauses);
            Assert.Equal(scenario.Failures, scenario.LateAnswers + scenario.StalePositions + scenario.OtherErrors);
            Assert.Equal(BudgetClassifier.Assess(scenario with { Assessment = null }).Passed, scenario.Assessment!.Passed);
            Assert.Equal(BudgetClassifier.Assess(scenario with { Assessment = null }).Checks.Select(check => (check.Name, check.Passed)),
                scenario.Assessment.Checks.Select(check => (check.Name, check.Passed)));
        }

        var recomputed = BudgetClassifier.Select(report.Scenarios);
        Assert.Equal(recomputed.LiveMaximum, report.Selection!.LiveMaximum);
        Assert.Equal(recomputed.Counts.Select(count => (count.Players, count.Passed)), report.Selection.Counts.Select(count => (count.Players, count.Passed)));
        if (report.Selection.LiveMaximum is { } maximum)
        {
            Assert.InRange(maximum, BenchmarkOptions.MinimumPlayers, BenchmarkOptions.MaximumPlayers);
            Assert.Contains(counts, count => count == maximum);
        }
    }

    [Fact]
    public void The_readable_analysis_states_the_selected_maximum_of_the_checked_in_report()
    {
        var report = JsonSerializer.Deserialize<BenchmarkReport>(File.ReadAllText(RepositoryFile("docs/platform/benchmark.raw.json")))!;
        var analysis = File.ReadAllText(RepositoryFile("docs/platform/benchmark.md"));

        Assert.Contains(report.SourceRevision, analysis, StringComparison.Ordinal);
        Assert.Contains(
            report.Selection!.LiveMaximum is { } maximum ? $"Selected live maximum: **{maximum}**" : "Selected live maximum: **none**",
            analysis,
            StringComparison.Ordinal);
    }

    private static IEnumerable<ScenarioReport> Matrix(int players, double ordinary = 1, double close = 10, double finish = 10, double drain = 500, int onTimeRefusals = 0) =>
        Enum.GetValues<LiveScenario>().Select(scenario => Report(scenario, players, ordinary, close, scenario == LiveScenario.Cold ? null : finish, drain, onTimeRefusals));

    private static ScenarioReport Report(
        LiveScenario scenario,
        int players,
        double? ordinary = 1,
        double? close = 10,
        double? finish = 10,
        double? drain = 500,
        int onTimeRefusals = 0,
        int withoutReceipt = 0,
        double? commitWait = 5)
    {
        var completed = drain is { } milliseconds ? Deadline.AddMilliseconds(milliseconds) : (DateTimeOffset?)null;
        var turns = new List<SessionTurnSample>();
        if (ordinary is { } ordinaryMs && completed is { } at) turns.Add(new(SessionTurnKind.OrdinaryAnswer, TimeSpan.FromMilliseconds(ordinaryMs), Deadline, at));
        if (close is { } closeMs) turns.Add(new(SessionTurnKind.RoundClose, TimeSpan.FromMilliseconds(closeMs), Deadline, Deadline));
        if (finish is { } finishMs) turns.Add(new(SessionTurnKind.Finish, TimeSpan.FromMilliseconds(finishMs), Deadline, Deadline, SessionTurnSource.Timer));
        var commands = new List<CommandSample>
        {
            new(Guid.NewGuid(), Deadline.AddMilliseconds(-5), Deadline.AddMilliseconds(-4), TimeSpan.FromMilliseconds(1), CommandOutcome.Accepted, null),
        };
        for (var refused = 0; refused < onTimeRefusals; refused++)
            commands.Add(new(Guid.NewGuid(), Deadline.AddMilliseconds(-5), Deadline.AddMilliseconds(-4), TimeSpan.FromMilliseconds(1), CommandOutcome.StalePosition, "http:200:command:stale_position"));
        for (var missing = 0; missing < withoutReceipt; missing++)
            commands.Add(new(Guid.NewGuid(), Deadline.AddMilliseconds(-5), null, TimeSpan.FromMilliseconds(1), CommandOutcome.TimedOut, "timeout:command-request"));

        return BenchmarkReportAssembler.Build(
            new BenchmarkOptions(players, scenario, null),
            viewsReceived: 0,
            Deadline,
            commands,
            turns,
            [],
            [],
            [],
            [],
            commitWait is { } wait ? [TimeSpan.FromMilliseconds(wait)] : []);
    }

    private static string RepositoryFile(string relative)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"{relative} was not found above {AppContext.BaseDirectory}.");
    }

    private static bool IsAncestorOfHead(string revision) => Git($"merge-base --is-ancestor {revision} HEAD");

    private static bool GitHasCommit(string revision) => Git($"cat-file -e {revision}^{{commit}}");

    private static bool Git(string arguments)
    {
        var repository = Path.GetDirectoryName(RepositoryFile("docs/platform/benchmark.raw.json"))!;
        using var process = Process.Start(new ProcessStartInfo("git", $"-C {repository} {arguments}")
        {
            UseShellExecute = false,
            RedirectStandardError = true
        });

        return process is not null && process.WaitForExit(5_000) && process.ExitCode == 0;
    }
}
