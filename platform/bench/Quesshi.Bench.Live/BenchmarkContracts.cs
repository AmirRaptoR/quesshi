using System.Text.Json;
using Quesshi.Application.Sessions;
using Quesshi.Engine.Contracts;
using Quesshi.Kinds.Choice;

namespace Quesshi.Bench.Live;

public enum LiveScenario { Burst, Spread, BurstStreams, Cold, Pool10 }
public sealed record BenchmarkOptions(int Players, LiveScenario Scenario, string? Output, bool Matrix = false, string? PostgreSql = null, bool DisposablePostgreSql = false, IReadOnlyList<int>? Counts = null, int Repetitions = 1)
{
    public const int MinimumPlayers = 2;
    public const int MaximumPlayers = 500;
    /// <summary>The candidate counts of the matrix: the lower bound, today's default, and a ladder up to the platform maximum.</summary>
    public static readonly int[] DefaultCounts = [2, 20, 50, 100, 150, 200, 250, 350, 500];
    public IReadOnlyList<int> MatrixCounts => Counts ?? DefaultCounts;

    public static BenchmarkOptions Parse(string[] args)
    {
        var players = 500; var scenario = LiveScenario.Burst; string? output = null; string? postgreSql = null; var matrix = false; var disposablePostgreSql = false;
        IReadOnlyList<int>? counts = null; var repetitions = 1;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--players" && ++i < args.Length) players = int.Parse(args[i]);
            else if (args[i] == "--scenario" && ++i < args.Length && !Enum.TryParse<LiveScenario>(args[i].Replace("-", ""), true, out scenario)) throw new ArgumentException("Unknown scenario.");
            else if (args[i] == "--output" && ++i < args.Length) output = args[i];
            else if (args[i] == "--postgres" && ++i < args.Length) postgreSql = args[i];
            else if (args[i] == "--counts" && ++i < args.Length) counts = args[i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).Distinct().Order().ToArray();
            else if (args[i] == "--repetitions" && ++i < args.Length) repetitions = int.Parse(args[i]);
            else if (args[i] == "--matrix") matrix = true;
            else if (args[i] == "--allow-disposable-postgres") disposablePostgreSql = true;
        }
        if (players is < MinimumPlayers or > MaximumPlayers) throw new ArgumentOutOfRangeException(nameof(args), $"--players must be {MinimumPlayers} to {MaximumPlayers}.");
        if (counts is not null && counts.Any(count => count is < MinimumPlayers or > MaximumPlayers)) throw new ArgumentOutOfRangeException(nameof(args), $"--counts must be {MinimumPlayers} to {MaximumPlayers}.");
        if (repetitions < 1) throw new ArgumentOutOfRangeException(nameof(args), "--repetitions must be at least 1.");
        return new(players, scenario, output, matrix, postgreSql, disposablePostgreSql, counts, repetitions);
    }
    public int StreamCount => Scenario == LiveScenario.BurstStreams ? 500 : 0;
    public int ItemCount => Scenario == LiveScenario.Cold ? 100 : 1;
    public int? PoolSize => Scenario == LiveScenario.Pool10 ? 10 : null;
    public int ApplicationPoolSize => PoolSize ?? 40;
}

public sealed record BenchmarkItem(string Prompt, IReadOnlyList<string> Options, int CorrectIndex)
{
    public static BenchmarkItem Create(int slot) => new(new string((char)('A' + slot % 26), 200), Enumerable.Range(0, 4).Select(i => $"{slot:D4}-{i:D2}" + new string((char)('a' + i), 73)).ToArray(), 0);
    public JsonElement Payload => JsonSerializer.SerializeToElement(new ChoicePayload(Options, CorrectIndex), ChoiceJsonContext.Default.ChoicePayload);
    public JsonElement Card => JsonSerializer.SerializeToElement(new ChoiceCard(Options), ChoiceJsonContext.Default.ChoiceCard);
    public JsonElement Reveal => JsonSerializer.SerializeToElement(new ChoiceReveal(CorrectIndex, Options[CorrectIndex]), ChoiceJsonContext.Default.ChoiceReveal);
}
public sealed record Distribution(int SampleCount, double? P50, double? P95, double? P99, double? Max, string Unit = "ms", string? UnavailableReason = null);
public enum CommandOutcome { Accepted, LateAnswer, StalePosition, Failed, TimedOut }
public sealed record CommandSample(Guid PlayerId, DateTimeOffset SentAt, DateTimeOffset? ReceivedAt, TimeSpan Latency, CommandOutcome Outcome, string? Error);
public sealed record CommandOutcomeSummary(int CommandsSent, int Accepted, int LateAnswers, int StalePositions, int OtherErrors, int Timeouts);
public sealed record GcActivity(int Gen0, int Gen1, int Gen2, double PauseMs);
public sealed record ScenarioReport(int Players, string Scenario, int Streams, int Items, int PoolConnections, int ViewsReceived, Distribution OrdinaryAnswerBusy, Distribution RoundCloseBusy, Distribution FinishBusy, Distribution CommandLatency, Distribution TimerLag, Distribution PoolWait, Distribution ColdActivationBusy, Distribution ColdActivationPoolWait, double? LastAcceptedCommittedAfterDeadlineMs, int OnTimeRefusedAtClose, int CommandsSent, int Accepted, int LateAnswers, int StalePositions, int OtherErrors, int Failures, int Timeouts, int CommandsWithoutServerReceipt, IReadOnlyDictionary<string, int> FailureCauses)
{
    /// <summary>Time from an ordinary answer being accepted to its batch committing (not part of the grain's busy time).</summary>
    public Distribution AnswerCommit { get; init; } = new(0, null, null, null, null, UnavailableReason: "not measured");
    /// <summary>Collections and pause time of the silo process during the measured window.</summary>
    public GcActivity? Gc { get; init; }
    public int Repetition { get; init; } = 1;
    public BudgetAssessment? Assessment { get; init; }
}
public sealed record BenchmarkRunEnvironment(string Hostname, string CpuModel, int LogicalProcessorCount, long AvailableMemoryBytes, string OperatingSystem, string DotNet, string PostgreSqlVersion, string PostgreSqlLocation);
public sealed record BenchmarkReport(string SourceRevision, DateTimeOffset RunAtUtc, string Command, BenchmarkRunEnvironment Environment, IReadOnlyList<ScenarioReport> Scenarios)
{
    public const string CurrentSchema = "quesshi.live-benchmark/2";
    public string Schema { get; init; } = CurrentSchema;
    public LiveMaximumSelection? Selection { get; init; }
}

public static class BenchmarkMeasurementClassifier
{
    public static CommandOutcome ResponseOutcome(string? code, bool httpSuccess) => code switch
    {
        "ok" when httpSuccess => CommandOutcome.Accepted,
        "late_answer" => CommandOutcome.LateAnswer,
        "stale_position" => CommandOutcome.StalePosition,
        _ => CommandOutcome.Failed,
    };

    public static int OnTimeRefusals(IEnumerable<CommandSample> commands, DateTimeOffset deadline) => commands.Count(command =>
        command.ReceivedAt <= deadline
        && (command.Outcome == CommandOutcome.LateAnswer || command.Outcome == CommandOutcome.StalePosition));

    public static CommandOutcomeSummary Summarize(IEnumerable<CommandSample> commands)
    {
        var samples = commands.ToArray();
        return new(
            samples.Length,
            samples.Count(sample => sample.Outcome == CommandOutcome.Accepted),
            samples.Count(sample => sample.Outcome == CommandOutcome.LateAnswer),
            samples.Count(sample => sample.Outcome == CommandOutcome.StalePosition),
            samples.Count(sample => sample.Outcome is CommandOutcome.Failed or CommandOutcome.TimedOut),
            samples.Count(sample => sample.Outcome == CommandOutcome.TimedOut));
    }
}

public static class ReportBuilder
{
    public static Distribution DistributionOf(IEnumerable<TimeSpan> samples, string? unavailable = null)
    {
        var values = samples.Select(x => x.TotalMilliseconds).OrderBy(x => x).ToArray();
        if (values.Length == 0) return new(0, null, null, null, null, UnavailableReason: unavailable ?? "no samples");
        double At(double percentile) => values[Math.Min(values.Length - 1, (int)Math.Ceiling(values.Length * percentile) - 1)];
        return new(values.Length, At(.50), At(.95), At(.99), values[^1]);
    }
}

public static class BenchmarkReportAssembler
{
    public static ScenarioReport Build(
        BenchmarkOptions options,
        int viewsReceived,
        DateTimeOffset questionDeadline,
        IEnumerable<CommandSample> commands,
        IEnumerable<SessionTurnSample> measurements,
        IEnumerable<TimeSpan> timerLags,
        IEnumerable<TimeSpan> poolWaits,
        IEnumerable<SessionTurnSample> coldActivation,
        IEnumerable<TimeSpan> coldPoolWaits,
        IEnumerable<TimeSpan>? answerCommits = null,
        GcActivity? gc = null)
    {
        var commandSamples = commands.ToArray();
        var turns = measurements.ToArray();
        var outcomes = BenchmarkMeasurementClassifier.Summarize(commandSamples);
        var close = turns.Where(sample => sample.Kind == SessionTurnKind.RoundClose).ToArray();
        var finish = turns.Where(sample => sample.Kind == SessionTurnKind.Finish).ToArray();
        var ordinary = turns.Where(sample => sample.Kind == SessionTurnKind.OrdinaryAnswer).ToArray();
        var committed = turns.Where(sample => sample.Source == SessionTurnSource.Command
            && sample.Kind is SessionTurnKind.OrdinaryAnswer or SessionTurnKind.RoundClose)
            .Select(sample => sample.CompletedAt)
            .DefaultIfEmpty()
            .Max();
        var failureCauses = commandSamples.Where(sample => sample.Outcome != CommandOutcome.Accepted)
            .GroupBy(sample => sample.Error ?? $"command:{sample.Outcome}", StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var report = new ScenarioReport(
            options.Players,
            options.Scenario.CliName(),
            options.StreamCount,
            options.ItemCount,
            options.ApplicationPoolSize,
            viewsReceived,
            ReportBuilder.DistributionOf(ordinary.Select(sample => sample.Duration), "no ordinary answers were accepted"),
            ReportBuilder.DistributionOf(close.Select(sample => sample.Duration), "no round close occurred"),
            ReportBuilder.DistributionOf(finish.Select(sample => sample.Duration), options.ItemCount == 1 ? "no finish occurred" : "cold activation stops after the first loaded slot"),
            ReportBuilder.DistributionOf(commandSamples.Select(command => command.Latency), "no commands were sent"),
            ReportBuilder.DistributionOf(timerLags, "no due timer fired"),
            ReportBuilder.DistributionOf(poolWaits, "no application-pool checkout occurred"),
            ReportBuilder.DistributionOf(coldActivation.Select(sample => sample.Duration), options.Scenario == LiveScenario.Cold ? "cold activation sample was not observed" : "not a cold activation scenario"),
            ReportBuilder.DistributionOf(coldPoolWaits, options.Scenario == LiveScenario.Cold ? "no application-pool checkout occurred during cold activation" : "not a cold activation scenario"),
            committed == default ? null : (committed - questionDeadline).TotalMilliseconds,
            BenchmarkMeasurementClassifier.OnTimeRefusals(commandSamples, questionDeadline),
            outcomes.CommandsSent,
            outcomes.Accepted,
            outcomes.LateAnswers,
            outcomes.StalePositions,
            outcomes.OtherErrors,
            outcomes.LateAnswers + outcomes.StalePositions + outcomes.OtherErrors,
            outcomes.Timeouts,
            commandSamples.Count(sample => sample.ReceivedAt is null),
            failureCauses)
        {
            AnswerCommit = ReportBuilder.DistributionOf(answerCommits ?? [], "no ordinary answer was committed"),
            Gc = gc,
        };
        return report with { Assessment = BudgetClassifier.Assess(report) };
    }
}
