using System.Text.Json;
using Dapper;
using Quesshi.Application.Sessions;
using Quesshi.Bench.Live;
using Quesshi.Engine.Contracts;
using Quesshi.Formats.Testing;
using Quesshi.Kinds.Choice;
using Quesshi.Shared;
using Quesshi.Infrastructure.Database;
using Quesshi.Server;
using Quesshi.Testing;
using Xunit.Sdk;

namespace Quesshi.Bench.Live.Tests;

[CollectionDefinition("PostgreSQL", DisableParallelization = true)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;

public sealed class BenchLiveFormatTests
{
    [Fact]
    public void Last_answer_closes_and_records_no_misses()
    {
        var (format, clock, session) = Create();
        format.Start(session); clock.Advance(BenchLiveFormat.Countdown); format.Timeout(session);
        Answer(format, session, clock, "a"); Answer(format, session, clock, "b");
        Assert.Equal(BenchLiveFormat.RevealPhase, session.Session.Position!.Phase);
        Assert.All(session.Seats, seat => Assert.NotNull(session.Answer(seat.Player, 0)));
    }

    [Fact]
    public void Deadline_plus_grace_closes_and_records_misses()
    {
        var (format, clock, session) = Create();
        format.Start(session); clock.Advance(BenchLiveFormat.Countdown); format.Timeout(session);
        clock.Advance(BenchLiveFormat.Question); format.Timeout(session);
        clock.Advance(BenchLiveFormat.Grace); format.Timeout(session);
        Assert.Equal(BenchLiveFormat.RevealPhase, session.Session.Position!.Phase);
        Assert.All(session.Seats, seat => Assert.True(session.Answer(seat.Player, 0)!.Miss));
    }

    [Fact]
    public void Reveal_of_last_item_finishes()
    {
        var (format, clock, session) = Create();
        format.Start(session); clock.Advance(BenchLiveFormat.Countdown); format.Timeout(session);
        Answer(format, session, clock, "a"); Answer(format, session, clock, "b");
        clock.Advance(BenchLiveFormat.Reveal); format.Timeout(session);
        Assert.True(session.Session.Ended);
    }

    [Fact]
    public void Fixture_and_scenarios_preserve_requested_load()
    {
        var item = BenchmarkItem.Create(1);
        Assert.Equal(200, item.Prompt.Length); Assert.Equal(4, item.Options.Count); Assert.All(item.Options, x => Assert.Equal(80, x.Length));
        Assert.Equal(4, item.Options.Distinct().Count());
        Assert.Equal(4, item.Payload.GetProperty("options").GetArrayLength());
        Assert.Equal(item.Options, item.Card.GetProperty("options").EnumerateArray().Select(option => option.GetString()));

        var scenarios = new[]
        {
            (LiveScenario.Burst, "burst"),
            (LiveScenario.Spread, "spread"),
            (LiveScenario.BurstStreams, "burst-streams"),
            (LiveScenario.Cold, "cold"),
            (LiveScenario.Pool10, "pool10")
        };
        foreach (var players in BenchmarkOptions.PlayerCounts)
        {
            foreach (var (scenario, scenarioName) in scenarios)
            {
                var options = BenchmarkOptions.Parse(["--players", players.ToString(), "--scenario", scenarioName]);
                Assert.Equal(players, options.Players);
                Assert.Equal(scenario == LiveScenario.BurstStreams ? 500 : 0, options.StreamCount);
                Assert.Equal(scenario == LiveScenario.Cold ? 100 : 1, options.ItemCount);
                Assert.Equal(scenario == LiveScenario.Pool10 ? 10 : null, options.PoolSize);
                Assert.Equal(scenario == LiveScenario.Pool10 ? 10 : 40, options.ApplicationPoolSize);
            }
        }

        var disposable = BenchmarkOptions.Parse(["--players", "20", "--scenario", "burst", "--allow-disposable-postgres"]);
        Assert.True(disposable.DisposablePostgreSql);

        var matrix = from scenario in Enum.GetValues<LiveScenario>()
                     from players in BenchmarkOptions.PlayerCounts
                     select new BenchmarkOptions(players, scenario, null);
        Assert.Equal(20, matrix.Count());
        Assert.Equal(Enum.GetValues<LiveScenario>(), matrix.Select(run => run.Scenario).Distinct());
        Assert.All(
            matrix.GroupBy(run => run.Scenario),
            runs => Assert.Equal(BenchmarkOptions.PlayerCounts, runs.Select(run => run.Players)));
    }

    [Fact]
    public void Structured_report_marks_unavailable_samples_and_calculates_percentiles()
    {
        var distribution = ReportBuilder.DistributionOf(new[] { 1, 2, 3, 4 }.Select(value => TimeSpan.FromMilliseconds(value)));
        Assert.Equal(4, distribution.SampleCount);
        Assert.Equal(2, distribution.P50); Assert.Equal(4, distribution.P95); Assert.Equal(4, distribution.P99); Assert.Equal(4, distribution.Max);
        Assert.Equal("ms", distribution.Unit);

        var unavailable = ReportBuilder.DistributionOf(Enumerable.Empty<TimeSpan>(), "cold activation does not finish all 100 slots");
        Assert.Equal(0, unavailable.SampleCount);
        Assert.Equal("cold activation does not finish all 100 slots", unavailable.UnavailableReason);
    }

    [Fact]
    public void Command_measurements_classify_responses_and_only_count_server_on_time_refusals()
    {
        var deadline = DateTimeOffset.UnixEpoch.AddSeconds(20);
        var player = Guid.NewGuid();
        var commands = new[]
        {
            new CommandSample(player, deadline.AddMilliseconds(-20), deadline.AddMilliseconds(-10), TimeSpan.FromMilliseconds(2), CommandOutcome.Accepted, null),
            new CommandSample(Guid.NewGuid(), deadline.AddMilliseconds(-20), deadline.AddMilliseconds(-5), TimeSpan.FromMilliseconds(3), CommandOutcome.StalePosition, null),
            new CommandSample(Guid.NewGuid(), deadline.AddMilliseconds(-20), deadline.AddMilliseconds(5), TimeSpan.FromMilliseconds(4), CommandOutcome.LateAnswer, null),
            new CommandSample(Guid.NewGuid(), deadline.AddMilliseconds(-20), null, TimeSpan.FromMilliseconds(5), CommandOutcome.Failed, "missing measurement headers"),
        };

        Assert.Equal(CommandOutcome.Accepted, BenchmarkMeasurementClassifier.ResponseOutcome("ok", true));
        Assert.Equal(CommandOutcome.StalePosition, BenchmarkMeasurementClassifier.ResponseOutcome("stale_position", true));
        Assert.Equal(CommandOutcome.LateAnswer, BenchmarkMeasurementClassifier.ResponseOutcome("late_answer", false));
        Assert.Equal(1, BenchmarkMeasurementClassifier.OnTimeRefusals(commands, deadline));

        var outcomes = BenchmarkMeasurementClassifier.Summarize(commands);
        Assert.Equal(4, outcomes.CommandsSent);
        Assert.Equal(1, outcomes.Accepted);
        Assert.Equal(1, outcomes.StalePositions);
        Assert.Equal(1, outcomes.LateAnswers);
        Assert.Equal(1, outcomes.OtherErrors);
    }

    [Fact]
    public void Report_assembly_uses_commit_completion_and_preserves_failure_evidence()
    {
        var deadline = DateTimeOffset.UnixEpoch.AddSeconds(20);
        var report = BenchmarkReportAssembler.Build(
            new BenchmarkOptions(100, LiveScenario.Cold, null),
            viewsReceived: 0,
            deadline,
            [
                new CommandSample(Guid.NewGuid(), deadline.AddMilliseconds(-2), deadline.AddMilliseconds(-1), TimeSpan.FromMilliseconds(1), CommandOutcome.Accepted, null),
                new CommandSample(Guid.NewGuid(), deadline.AddMilliseconds(-2), null, TimeSpan.FromMilliseconds(2), CommandOutcome.Failed, "http:503:missing-command-code"),
            ],
            [
                new SessionTurnSample(SessionTurnKind.OrdinaryAnswer, TimeSpan.FromMilliseconds(12), deadline, deadline.AddMilliseconds(41)),
                new SessionTurnSample(SessionTurnKind.RoundClose, TimeSpan.FromMilliseconds(17), deadline, deadline.AddMilliseconds(43)),
                new SessionTurnSample(SessionTurnKind.RoundClose, TimeSpan.FromMilliseconds(9), deadline, deadline.AddMilliseconds(44), SessionTurnSource.Timer),
            ],
            [], [],
            [new SessionTurnSample(SessionTurnKind.ColdActivation, TimeSpan.FromMilliseconds(18), null, deadline)],
            [TimeSpan.FromMilliseconds(2)]);

        Assert.Equal(100, report.Items);
        Assert.Equal(40, report.PoolConnections);
        Assert.Equal(1, report.ColdActivationBusy.SampleCount);
        Assert.Equal(2, report.CommandsSent);
        Assert.Equal(1, report.Failures);
        Assert.Equal(1, report.CommandsWithoutServerReceipt);
        Assert.Equal(1, report.FailureCauses["http:503:missing-command-code"]);
        Assert.Equal(43, report.LastAcceptedCommittedAfterDeadlineMs);
        Assert.Equal(2, report.RoundCloseBusy.SampleCount);
    }

    private static (BenchLiveFormat Format, TestClock Clock, InMemorySession Session) Create()
    {
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var item = BenchmarkItem.Create(0);
        var state = SessionState.Create(new SessionRow("s", clock.Now, null, null, Format: "bench_live"), JsonSerializer.SerializeToElement(new { }),
            [new Seat("a", "player"), new Seat("b", "player")],
            [SessionItemState.Create(new SessionItem(0, "choice", 1, null, null), item.Payload, JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { }))]);
        return (new BenchLiveFormat(), clock, new InMemorySession(state, new KindRegistry([new ChoiceKind()]), clock, "a"));
    }
    private static void Answer(BenchLiveFormat format, InMemorySession session, TestClock clock, string player) =>
        format.Apply(session, new Command(CommandType.Answer, player, session.Session.Position!, 0, null, JsonSerializer.SerializeToElement(new { index = 0 }), clock.Now));
}

[Collection("PostgreSQL")]
public sealed class BenchmarkFixtureDatabaseTests(PostgresFixture fixture)
{
    [SkippableFact]
    public async Task Cold_fixture_persists_one_hundred_choice_items_and_requested_seats_through_production_snapshot_and_grading()
    {
        Skip.If(fixture.ConnectionString is null, $"Set {PostgresFixture.AdminConnectionStringEnvironmentVariable} to run PostgreSQL tests locally.");
        var ownerConnection = fixture.ConnectionStringForRole("quesshi_owner");
        await Quesshi.Server.Program.MigrateAsync(ownerConnection);
        await using var owner = new Npgsql.NpgsqlConnection(ownerConnection);
        await owner.OpenAsync();

        foreach (var players in BenchmarkOptions.PlayerCounts)
        {
            var tenant = await BenchmarkFixture.SeedTenantAsync(ownerConnection, $"bench-{players}.test");
            var playerIds = Enumerable.Range(0, players).Select(_ => Guid.NewGuid()).ToArray();
            await owner.ExecuteAsync("insert into players (tenant_id, id, display_name, avatar_seed, lang) select @tenant, id, 'Benchmark player', 'bench', 'en' from unnest(@players) as id", new { tenant = tenant.Id, players = playerIds });
            var session = await BenchmarkFixture.SeedSessionAsync(ownerConnection, tenant, new BenchmarkOptions(players, LiveScenario.Cold, null), playerIds);

            await using var sources = new DatabaseSources(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));
            var loader = new SessionSnapshotLoader(new TenantDb(sources));
            var state = Assert.IsType<SessionState>(await loader.LoadAsync(tenant.Id, session.Id));
            var items = await loader.LoadItemsAsync(tenant.Id, session.Id, Enumerable.Range(0, 100).ToArray());
            var persisted = (await owner.QueryAsync<(string Prompt, string Payload)>("select prompt, payload::text from session_items where session_id = @session order by slot", new { session = session.Id })).ToArray();

            Assert.Equal(players, state.Seats.Count);
            Assert.Equal(100, state.ItemCount);
            Assert.Equal(100, items.Count);
            Assert.Equal(100, persisted.Length);
            Assert.All(persisted, item => Assert.Equal(200, item.Prompt.Length));
            Assert.Equal(BenchmarkItem.Create(0).Prompt, persisted[0].Prompt);
            Assert.All(items, item =>
            {
                Assert.Equal("choice", item.Metadata.Kind);
                Assert.Equal(4, item.Payload.GetProperty("options").GetArrayLength());
                Assert.All(item.Payload.GetProperty("options").EnumerateArray(), option => Assert.Equal(80, option.GetString()!.Length));
            });

            IKindRuntime runtime = new ChoiceKind();
            var first = items[0];
            Assert.Equal(1, runtime.Grade(first.Payload, first.Resolved, JsonSerializer.SerializeToElement(new { index = 0 })).Credit);
        }
    }
}
