using System.Diagnostics;
using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;
using Quesshi.Server.Api;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

/// <summary>
/// The live duel's half of issue #73, played rather than constructed: a real duel whose first round
/// is a sorting question and whose second is a map one, driven through the same silo and the same
/// clock as every other live test.
/// <para>
/// Two things need a real duel to be worth asserting. The first is that the round-start push seeds
/// its shuffle with the duel's own id — a builder that seeded with anything else would still agree
/// with itself, and only a duel with a known id catches it. The second is the redaction rule, which
/// is a property of <c>LiveMatchGrain.ViewAsync</c> at a particular moment in a duel's life: the
/// round in flight reveals neither the answer nor another player's response, and that has to hold
/// for the new kinds exactly as it always has for <c>Choice</c>. A hand-built view cannot be caught
/// lying about a moment it never lived through.
/// </para>
/// </summary>
[Collection(nameof(LiveClusterCollection))]
public class LiveRedactionKindTests(LiveClusterFixture fixture)
{
    private const string Amir = "lrk-amir";
    private const string Sara = "lrk-sara";

    private static readonly List<string> Rivers = ["Nile", "Amazon", "Yangtze", "Mississippi"];

    /// <summary>A duel that opens with a sort, follows with a map, and fills out with choice questions.</summary>
    private static List<string> SeedMixed(string prefix)
    {
        if (LiveShared.Categories.Items.All(c => c.Id != "geography"))
            LiveShared.Categories.Items.Add(new Category("geography", "جغرافیا", "Geography", "globe", "#336699"));

        var now = LiveShared.TimeProvider.GetUtcNow();
        var ids = new List<string>();
        for (var slot = 0; slot < MatchRules.QuestionsPerMatch; slot++)
        {
            var qid = $"{prefix}-q{slot}";
            var level = MatchRules.LevelForSlot(slot);
            LiveShared.Questions.Items.Add(slot switch
            {
                0 => Question.Create(qid, Language.En, "geography", level,
                    "Order these rivers by length, longest first.", Rivers, 0, now,
                    explanation: "because", status: QuestionStatus.Approved, kind: QuestionKind.Sort),
                1 => Question.Create(qid, Language.En, "geography", level,
                    "Find the country whose capital is Berlin.", [], 0, now,
                    explanation: "because", status: QuestionStatus.Approved, kind: QuestionKind.Map,
                    target: MapTarget.Country("DE"), baseLayer: MapBaseLayer.Borders),
                _ => Question.Create(qid, Language.En, "geography", level,
                    $"question {slot}", ["right", "wrong1", "wrong2", "wrong3"], 0, now,
                    explanation: "because", status: QuestionStatus.Approved)
            });
            ids.Add(qid);
        }
        return ids;
    }

    /// <summary>
    /// A fresh duel id whose round-0 shuffle actually shuffles. One id in twenty-four leaves all four
    /// items where they were, and a test asserting that the card is <i>not</i> the stored order would
    /// then fail on a duel that behaved perfectly — an unreproducible failure once every few hundred
    /// runs, which is worse than the seed being slightly chosen.
    /// </summary>
    private static string NewShuffledId()
    {
        while (true)
        {
            var id = Guid.NewGuid().ToString("N");
            if (!SortOrder.IsIdentity(SortOrder.For(id, 0, MatchRules.ChoicesPerQuestion).Served)) return id;
        }
    }

    private async Task<(ILiveMatchGrain Grain, string Id)> StartedDuelAsync()
    {
        var id = NewShuffledId();
        var questionIds = SeedMixed(id);
        var grain = fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(id);

        await grain.CreateAsync(id[..8].ToUpperInvariant(), (int)Language.En, Amir, questionIds);
        Assert.True(await grain.JoinAsync(Sara) is (int)LiveJoinResult.Joined or (int)LiveJoinResult.AlreadyIn);
        await grain.StartAsync(Amir);

        LiveShared.TimeProvider.Advance(LiveRules.StartCountdown + TimeSpan.FromMilliseconds(50));
        await WaitForAsync(grain, v => v.Phase == (int)LivePhase.Question && v.RoundIndex == 0);

        return (grain, id);
    }

    private static async Task<LiveView> WaitForAsync(ILiveMatchGrain grain, Func<LiveView, bool> ready, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        LiveView? last = null;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            last = await grain.GetAsync(Amir);
            if (last is not null && ready(last)) return last;
            await Task.Delay(10);
        }
        throw new TimeoutException($"Condition not reached within {timeoutMs}ms. Last phase={last?.Phase}, rounds={last?.Rounds.Count}.");
    }

    private static LiveRoundCard CardFor(string matchId, int slot)
        => LiveShared.Notifier.EventsFor(matchId)
            .Where(e => e.Kind == "RoundStarted").Select(e => (LiveRoundCard)e.Payload)
            .Single(c => c.Slot == slot);

    private static LiveRoundReveal RevealFor(string matchId, int slot)
        => LiveShared.Notifier.EventsFor(matchId)
            .Where(e => e.Kind == "RoundRevealed").Select(e => (LiveRoundReveal)e.Payload)
            .Single(r => r.Slot == slot);

    /// <summary>
    /// How a round of each kind is actually played: a sorting round takes served positions in the
    /// order the player placed them, a map round a country code, an ordinary round a choice index.
    /// <para>
    /// Issue #74 is what makes this necessary — the grain now refuses a sorting or map round anything
    /// but its own shape, so these rounds are genuinely <i>played</i> rather than merely constructed
    /// with a stored answer. The two players deliberately disagree, so a reveal that mixed their
    /// answers up would show.
    /// </para>
    /// </summary>
    private static (int ChoiceIndex, string? Response) PlayFor(int slot, bool first) => slot switch
    {
        0 => (-1, first ? "0,1,2,3" : "3,2,1,0"),
        1 => (-1, first ? "DE" : "FR"),
        _ => (first ? 0 : 1, (string?)null)
    };

    /// <summary>Both answer, which closes the round at once and needs no buzzer.</summary>
    private static async Task CloseRoundAsync(ILiveMatchGrain grain, int slot)
    {
        var (amirChoice, amirResponse) = PlayFor(slot, first: true);
        var (saraChoice, saraResponse) = PlayFor(slot, first: false);

        Assert.True(await grain.AnswerAsync(Amir, slot, amirChoice, amirResponse));
        Assert.True(await grain.AnswerAsync(Sara, slot, saraChoice, saraResponse));
        await WaitForAsync(grain, v => v.Phase == (int)LivePhase.Reveal || v.State != (int)MatchState.InProgress);
    }

    private static async Task AdvanceToNextRoundAsync(ILiveMatchGrain grain, int nextSlot)
    {
        LiveShared.TimeProvider.Advance(LiveRules.RevealTime + TimeSpan.FromMilliseconds(50));
        await WaitForAsync(grain, v => v.Phase == (int)LivePhase.Question && v.RoundIndex == nextSlot);
    }

    [Fact]
    public async Task The_round_start_push_shuffles_a_sorting_question_by_this_duels_own_id_and_slot()
    {
        var (_, id) = await StartedDuelAsync();

        var card = CardFor(id, 0);

        Assert.Equal(QuestionKind.Sort, card.Kind);
        Assert.Equal(SortOrder.For(id, 0, Rivers.Count).Shuffle(Rivers), card.Choices);
        Assert.NotEqual(Rivers, card.Choices);
        Assert.Null(card.BaseLayer);
        Assert.Null(card.TargetShape);
    }

    [Fact]
    public async Task The_round_start_push_gives_a_map_round_its_layer_and_shape_and_not_its_target()
    {
        var (grain, id) = await StartedDuelAsync();
        await CloseRoundAsync(grain, 0);
        await AdvanceToNextRoundAsync(grain, 1);

        var card = CardFor(id, 1);

        Assert.Equal(QuestionKind.Map, card.Kind);
        Assert.Equal(MapBaseLayer.Borders, card.BaseLayer);
        Assert.Equal(MapTargetKind.Country, card.TargetShape);
        Assert.Empty(card.Choices);

        // The target itself never left the server, under any name.
        Assert.DoesNotContain("DE", System.Text.Json.JsonSerializer.Serialize(card.ToDto()));
    }

    /// <summary>
    /// The reveal is in stored terms and the grain never touches the seed to build it. Asserting
    /// that the correct order is <i>not</i> the served one is the whole of that claim: those two
    /// differ precisely when the shuffle did something, which is what makes this a real check rather
    /// than a coincidence.
    /// </summary>
    [Fact]
    public async Task The_reveal_push_names_the_stored_order_of_a_sort_not_the_order_the_card_served()
    {
        var (grain, id) = await StartedDuelAsync();
        var served = CardFor(id, 0).Choices;

        await CloseRoundAsync(grain, 0);

        var reveal = RevealFor(id, 0);
        Assert.Equal(QuestionKind.Sort, reveal.Kind);
        Assert.Equal(Rivers, reveal.CorrectOrder);
        Assert.NotEqual(served, reveal.CorrectOrder);
        Assert.Null(reveal.CorrectTarget);

        var dto = reveal.ToDto();
        Assert.Equal((int)QuestionKind.Sort, dto.Kind);
        Assert.Equal(Rivers, dto.CorrectOrder);
    }

    [Fact]
    public async Task The_reveal_push_names_a_map_rounds_target()
    {
        var (grain, id) = await StartedDuelAsync();
        await CloseRoundAsync(grain, 0);
        await AdvanceToNextRoundAsync(grain, 1);
        await CloseRoundAsync(grain, 1);

        var reveal = RevealFor(id, 1);

        Assert.Equal(QuestionKind.Map, reveal.Kind);
        Assert.Equal("DE", reveal.CorrectTarget);
        Assert.Null(reveal.CorrectOrder);
    }

    /// <summary>
    /// The redaction rule for the new kinds, at the one moment it matters: a client that reconnects
    /// while a sorting round is open gets the round back with no answer in it at all — not the
    /// correct order, not the target, and not the correct index it never carried either.
    /// </summary>
    [Fact]
    public async Task A_sorting_round_in_flight_reveals_neither_its_order_nor_the_other_players_answer()
    {
        var (grain, _) = await StartedDuelAsync();

        // One player has locked in and the other has not: the round is open, and the answer that is
        // already in must not leak to the player who has not answered yet.
        Assert.True(await grain.AnswerAsync(Sara, 0, -1, "3,2,1,0"));

        var view = await grain.GetAsync(Amir);
        var round = view!.Rounds[0];

        Assert.Null(round.CorrectIndex);
        Assert.Null(round.CorrectOrder);
        Assert.Null(round.CorrectTarget);

        var sara = round.Answers.Single(a => a.PlayerId == Sara);
        Assert.True(sara.Answered);
        Assert.Null(sara.ChoiceIndex);
        Assert.Null(sara.Correct);
        Assert.Null(sara.Response);
    }

    [Fact]
    public async Task A_map_round_in_flight_reveals_neither_its_target_nor_the_other_players_pin()
    {
        var (grain, _) = await StartedDuelAsync();
        await CloseRoundAsync(grain, 0);
        await AdvanceToNextRoundAsync(grain, 1);

        Assert.True(await grain.AnswerAsync(Sara, 1, -1, "FR"));

        var view = await grain.GetAsync(Amir);
        var round = view!.Rounds[1];

        Assert.Null(round.CorrectIndex);
        Assert.Null(round.CorrectTarget);
        Assert.Null(round.CorrectOrder);
        Assert.Null(round.Answers.Single(a => a.PlayerId == Sara).Response);
    }

    /// <summary>
    /// And the other side of the same rule: once the round is closed, the history it leaves behind
    /// carries the kind and the answer, so a reconnecting client can render a finished sort or map
    /// round rather than a blank one.
    /// </summary>
    [Fact]
    public async Task Once_a_round_closes_its_kind_and_answer_are_in_the_history()
    {
        var (grain, _) = await StartedDuelAsync();
        await CloseRoundAsync(grain, 0);
        await AdvanceToNextRoundAsync(grain, 1);
        await CloseRoundAsync(grain, 1);
        await AdvanceToNextRoundAsync(grain, 2);

        var view = await grain.GetAsync(Amir);

        Assert.Equal((int)QuestionKind.Sort, view!.Rounds[0].Kind);
        Assert.Equal(Rivers, view.Rounds[0].CorrectOrder);
        Assert.Equal((int)QuestionKind.Map, view.Rounds[1].Kind);
        Assert.Equal("DE", view.Rounds[1].CorrectTarget);

        // The round now in flight keeps the default kind, because the grain does not so much as load
        // its question while it is open — the card it pushed is where a client reads that.
        Assert.Equal((int)QuestionKind.Choice, view.Rounds[2].Kind);
        Assert.Null(view.Rounds[2].CorrectOrder);
    }
}
