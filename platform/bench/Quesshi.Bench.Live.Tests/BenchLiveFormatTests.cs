using System.Text.Json;
using Quesshi.Application.Sessions;
using Quesshi.Bench.Live;
using Quesshi.Engine.Contracts;
using Quesshi.Formats.Testing;
using Quesshi.Kinds.Choice;
using Quesshi.Shared;

namespace Quesshi.Bench.Live.Tests;

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
            }
        }
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
