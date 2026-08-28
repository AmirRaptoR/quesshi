using System.Diagnostics;
using System.Text.Json;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;
using Quesshi.Server.Api;
using Quesshi.Shared;
using Xunit.Abstractions;

namespace Quesshi.Server.Tests;

/// <summary>
/// The match list is the page a player opens most and the one that got slower the more they played:
/// it activated one grain and made up to two player lookups per archived duel, awaited in turn.
/// These tests pin the cost down rather than the wording — call counts, order, and the live numbers.
/// </summary>
[Collection(nameof(ClusterCollection))]
public class MatchListTests(ClusterFixture fixture, ITestOutputHelper output)
{
    // Each test gets its own pair of players. They share one archive, and the list only ever looks
    // at forty rows, so a shared player id would let one test's duels crowd out another's.
    private static string MeOf(string prefix) => $"p-{prefix}-me";
    private static string RivalOf(string prefix) => $"p-{prefix}-rival";

    private IGrainFactory Grains => fixture.Cluster.GrainFactory;

    /// <summary>Six questions whose right answer is always index 0, so a run can be steered.</summary>
    private static List<string> SeedQuestions(string prefix)
    {
        var ids = new List<string>();
        for (var slot = 0; slot < MatchRules.QuestionsPerMatch; slot++)
        {
            var qid = $"{prefix}-q{slot}";
            Shared.Questions.Items.Add(Question.Create(qid, Language.En, "geography", MatchRules.LevelForSlot(slot),
                $"question {slot}", ["right", "wrong1", "wrong2", "wrong3"], 0, Shared.Clock.Now,
                status: QuestionStatus.Approved));
            ids.Add(qid);
        }
        return ids;
    }

    private static async Task PlayAsync(IMatchGrain grain, string player, int correctCount)
    {
        for (var slot = 0; slot < MatchRules.QuestionsPerMatch; slot++)
        {
            var served = await grain.ServeNextAsync(player);
            await grain.AnswerAsync(player, served!.Slot, slot < correctCount ? 0 : 1);
        }
    }

    /// <summary>A player with `count` duels behind them; the first `resolved` are carried to a result.</summary>
    private async Task SeedAsync(int count, int resolved, string prefix)
    {
        var (me, rival) = (MeOf(prefix), RivalOf(prefix));
        await Shared.Players.UpsertAsync(Player.Register(me, $"{prefix}-me@example.com", "Amir", Language.En, Shared.Clock.Now));
        await Shared.Players.UpsertAsync(Player.Register(rival, $"{prefix}-rival@example.com", "Sara", Language.En, Shared.Clock.Now));

        for (var i = 0; i < count; i++)
        {
            var id = $"{prefix}-m{i}";
            var grain = Grains.GetGrain<IMatchGrain>(id);
            await grain.CreateAsync((int)Language.En, me, SeedQuestions(id), $"{prefix}{i:D3}".ToUpperInvariant());
            await grain.JoinAsync(rival);
            if (i >= resolved) continue;
            await PlayAsync(grain, me, correctCount: 4);
            await PlayAsync(grain, rival, correctCount: 2);
        }
    }

    /// <summary>The implementation this change replaces, kept as the thing the new one must match.</summary>
    private async Task<List<MatchSummaryDto>> SequentialListAsync(string meId)
    {
        var rows = await Shared.Archive.ForPlayerAsync(meId, 40);

        var summaries = new List<MatchSummaryDto>();
        foreach (var row in rows)
        {
            if (await Grains.GetGrain<IMatchGrain>(row.Id).GetAsync(meId) is not { } view) continue;

            var names = new Dictionary<string, (string, string)>();
            foreach (var id in new[] { view.ChallengerId, view.OpponentId }.OfType<string>().Distinct())
            {
                var p = await Shared.Players.GetAsync(id);
                names[id] = (p?.DisplayName ?? "—", p?.AvatarSeed ?? id);
            }

            summaries.Add(view.ToSummary(meId, id => names.GetValueOrDefault(id, ("—", id))));
        }
        return summaries;
    }

    [Fact]
    public async Task The_list_reads_the_stores_twice_however_many_duels_there_are()
    {
        await SeedAsync(26, resolved: 2, "qc");
        var me = MeOf("qc");
        Shared.Archive.ResetCounters();
        Shared.Players.ResetCounters();

        var list = await GameEndpoints.ListMatchesAsync(me, activeOnly: false, Shared.Archive, Shared.Players, Grains);

        Assert.True(list.Count >= 26, $"expected at least 26 duels, got {list.Count}");
        Assert.Equal(1, Shared.Archive.Queries);
        Assert.Equal(1, Shared.Players.Queries);
    }

    [Fact]
    public async Task The_batched_list_returns_exactly_what_the_sequential_one_did()
    {
        await SeedAsync(26, resolved: 3, "eq");
        var me = MeOf("eq");

        var expected = await SequentialListAsync(me);
        var actual = await GameEndpoints.ListMatchesAsync(me, activeOnly: false, Shared.Archive, Shared.Players, Grains);

        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
    }

    [Fact]
    public async Task A_duel_in_progress_still_reports_the_live_counters()
    {
        await SeedAsync(1, resolved: 0, "live");
        var me = MeOf("live");
        var grain = Grains.GetGrain<IMatchGrain>("live-m0");
        var served = await grain.ServeNextAsync(me);
        await grain.AnswerAsync(me, served!.Slot, 0);

        var list = await GameEndpoints.ListMatchesAsync(me, activeOnly: false, Shared.Archive, Shared.Players, Grains);

        var mine = list.Single(m => m.Id == "live-m0");
        Assert.Equal(1, mine.Me.Answered);
        Assert.Equal(1, mine.Me.Correct);
        Assert.False(mine.Me.Finished);
    }

    [Fact]
    public async Task The_opponents_score_stays_hidden_until_the_reveal()
    {
        await SeedAsync(1, resolved: 0, "hide");
        var me = MeOf("hide");
        var grain = Grains.GetGrain<IMatchGrain>("hide-m0");
        await PlayAsync(grain, RivalOf("hide"), correctCount: 6);

        var list = await GameEndpoints.ListMatchesAsync(me, activeOnly: false, Shared.Archive, Shared.Players, Grains);

        var mine = list.Single(m => m.Id == "hide-m0");
        Assert.False(mine.CanReveal);
        Assert.Equal(0, mine.Opponent!.Score);
    }

    [Fact]
    public async Task Asking_for_the_active_duels_leaves_the_finished_ones_alone()
    {
        await SeedAsync(4, resolved: 2, "act");
        var me = MeOf("act");

        var active = await GameEndpoints.ListMatchesAsync(me, activeOnly: true, Shared.Archive, Shared.Players, Grains);

        Assert.All(active, m => Assert.True(m.State is "awaitingopponent" or "inprogress", $"got state {m.State}"));
        Assert.DoesNotContain(active, m => m.Id == "act-m0");
        Assert.Contains(active, m => m.Id == "act-m2");
    }

    [Fact]
    public async Task An_opponent_the_archive_has_not_caught_up_with_is_still_named()
    {
        await SeedAsync(1, resolved: 0, "lag");
        var me = MeOf("lag");

        // A grain persists itself before it is mirrored into Mongo, so this is the state the list
        // can genuinely observe between the two writes: the duel has an opponent, the row does not.
        var row = Shared.Archive.Items.Single(m => m.Id == "lag-m0");
        await Shared.Archive.SaveAsync(row with { OpponentId = null, OpponentScore = 0, State = MatchState.AwaitingOpponent });

        var list = await GameEndpoints.ListMatchesAsync(me, activeOnly: false, Shared.Archive, Shared.Players, Grains);

        var mine = list.Single(m => m.Id == "lag-m0");
        Assert.NotNull(mine.Opponent);
        Assert.Equal("Sara", mine.Opponent!.DisplayName);
    }

    /// <summary>
    /// The number the issue asks for. Each fake store call sleeps a millisecond, standing in for a
    /// round trip, so the comparison is between one shape and the other rather than between two
    /// in-memory dictionaries. Not a production profile — a model of what serialising costs.
    /// </summary>
    [Fact]
    public async Task The_batched_list_is_far_faster_when_the_stores_are_not_free()
    {
        await SeedAsync(40, resolved: 4, "perf");
        var me = MeOf("perf");
        Shared.Archive.DelayMs = 1;
        Shared.Players.DelayMs = 1;
        try
        {
            var before = await MedianMillisecondsAsync(() => SequentialListAsync(me));
            var after = await MedianMillisecondsAsync(() =>
                GameEndpoints.ListMatchesAsync(me, false, Shared.Archive, Shared.Players, Grains));

            var ratio = before / Math.Max(after, 1);
            output.WriteLine($"40 duels, 1ms per store round trip: sequential {before}ms, "
                + $"batched {after}ms, {ratio:F1}x");
            Assert.True(ratio >= 5,
                $"sequential {before}ms vs batched {after}ms = {ratio:F1}x, wanted 5x");
        }
        finally
        {
            Shared.Archive.DelayMs = 0;
            Shared.Players.DelayMs = 0;
        }
    }

    /// <summary>
    /// Runs the thing four times and takes the median of the last three. A shared test cluster and a
    /// loaded machine both make single measurements jumpy; the shape being measured does not change.
    /// </summary>
    private static async Task<double> MedianMillisecondsAsync(Func<Task> run)
    {
        await run();                       // warm
        var samples = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            var watch = Stopwatch.StartNew();
            await run();
            watch.Stop();
            samples.Add(watch.ElapsedMilliseconds);
        }
        samples.Sort();
        return samples[1];
    }
}
