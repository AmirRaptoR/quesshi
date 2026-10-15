using System.Text.Json;
using Quesshi.Engine.Contracts;
using Quesshi.Kinds.Choice;

namespace Quesshi.Bench.Live;

public enum LiveScenario { Burst, Spread, BurstStreams, Cold, Pool10 }
public sealed record BenchmarkOptions(int Players, LiveScenario Scenario, string? Output, bool Matrix = false, string? PostgreSql = null, bool DisposablePostgreSql = false)
{
    public static readonly int[] PlayerCounts = [20, 100, 250, 500];
    public static BenchmarkOptions Parse(string[] args)
    {
        var players = 500; var scenario = LiveScenario.Burst; string? output = null; string? postgreSql = null; var matrix = false; var disposablePostgreSql = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--players" && ++i < args.Length) players = int.Parse(args[i]);
            else if (args[i] == "--scenario" && ++i < args.Length && !Enum.TryParse<LiveScenario>(args[i].Replace("-", ""), true, out scenario)) throw new ArgumentException("Unknown scenario.");
            else if (args[i] == "--output" && ++i < args.Length) output = args[i];
            else if (args[i] == "--postgres" && ++i < args.Length) postgreSql = args[i];
            else if (args[i] == "--matrix") matrix = true;
            else if (args[i] == "--allow-disposable-postgres") disposablePostgreSql = true;
        }
        if (!PlayerCounts.Contains(players)) throw new ArgumentOutOfRangeException(nameof(args), "--players must be 20, 100, 250, or 500.");
        return new(players, scenario, output, matrix, postgreSql, disposablePostgreSql);
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
public sealed record ScenarioReport(int Players, string Scenario, int Streams, int Items, int PoolConnections, int ViewsReceived, Distribution OrdinaryAnswerBusy, Distribution RoundCloseBusy, Distribution FinishBusy, Distribution CommandLatency, Distribution TimerLag, Distribution PoolWait, Distribution ColdActivationBusy, Distribution ColdActivationPoolWait, double? LastAcceptedAfterDeadlineMs, int OnTimeRefusedAtClose, int CommandsSent, int Accepted, int LateAnswers, int StalePositions, int OtherErrors, int Failures, int Timeouts);
public sealed record BenchmarkReport(string SourceRevision, DateTimeOffset RunAtUtc, string Command, string Machine, string Environment, IReadOnlyList<ScenarioReport> Scenarios);

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
