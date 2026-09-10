using System.Diagnostics;
using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;

namespace Quesshi.Server.Tests;

/// <summary>
/// Two things issue #52 owes an N-player duel that issue #51 (grains) left half-done, both proven
/// through the real <see cref="ILiveMatchGrain"/> against <see cref="LiveShared.Notifier"/>, the same
/// way <see cref="LiveMatchGrainTests"/> proves every other push:
///
/// 1. <c>LiveMatchGrain</c>'s internal <c>Participants</c> helper fed every view and notification
///    builder off the obsolete two-scalar <c>ChallengerId</c>/<c>OpponentId</c> pair, silently
///    truncating a capacity-&gt;2 duel's roster back down to two on the way out even though the domain
///    and grain API are fully N-player already.
/// 2. Nothing told a client when <c>LiveMatch.CloseRound</c> drops a player to the miss-streak — the
///    elimination wire contract this issue adds as <c>ILiveNotifier.PlayerEliminatedAsync</c>.
/// </summary>
[Collection(nameof(LiveClusterCollection))]
public class LiveEliminationTests(LiveClusterFixture fixture)
{
    private const string Amir = "let-amir";
    private const string Sara = "let-sara";
    private const string Vahid = "let-vahid";
    private const string Category = "let-geography";

    private async Task<(ILiveMatchGrain Grain, string Id)> NewLobbyAsync(string code, string owner, int capacity)
    {
        var id = Guid.NewGuid().ToString("N");
        SeedQuestions(id);
        var grain = fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(id);
        await grain.CreateLobbyAsync(code, owner, (int)Language.En, MatchRules.QuestionsPerMatch, [], [], capacity);
        return (grain, id);
    }

    /// <summary>Ten questions where the correct answer is always index 0 — mirrors
    /// <see cref="LiveMatchGrainTests"/>'s own pool, with this class's own category and id prefix so
    /// the shared static pool the whole <see cref="LiveClusterCollection"/> draws from cannot collide.</summary>
    private static void SeedQuestions(string prefix)
    {
        if (LiveShared.Categories.Items.All(c => c.Id != Category))
            LiveShared.Categories.Items.Add(new Category(Category, "جغرافیا", "Geography", "globe", "#336699"));

        for (var slot = 0; slot < MatchRules.QuestionsPerMatch; slot++)
        {
            var qid = $"{prefix}-q{slot}";
            if (LiveShared.Questions.Items.Any(q => q.Id == qid)) continue;
            LiveShared.Questions.Items.Add(Question.Create(qid, Language.En, Category, MatchRules.LevelForSlot(slot),
                $"question {slot}", ["right", "wrong1", "wrong2", "wrong3"], 0, LiveShared.TimeProvider.GetUtcNow(),
                explanation: "because", status: QuestionStatus.Approved));
        }
    }

    private static void Advance(TimeSpan by) => LiveShared.TimeProvider.Advance(by);

    private static async Task<LiveView> WaitForAsync(ILiveMatchGrain grain, string asPlayer, Func<LiveView, bool> ready, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        LiveView? last = null;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            last = await grain.GetAsync(asPlayer);
            if (last is not null && ready(last)) return last;
            await Task.Delay(10);
        }
        throw new TimeoutException($"Condition not reached within {timeoutMs}ms. Last phase={last?.Phase}, state={last?.State}, rounds={last?.Rounds.Count}.");
    }

    private static async Task WaitForEventAsync(string matchId, string kind, int count, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (LiveShared.Notifier.EventsFor(matchId).Count(e => e.Kind == kind) >= count) return;
            await Task.Delay(10);
        }
        throw new TimeoutException($"Never saw {count} '{kind}' events for {matchId}.");
    }

    [Fact]
    public async Task A_full_capacity_three_lobby_reports_all_three_participants_not_just_two()
    {
        var (grain, _) = await NewLobbyAsync("LET-CAP3", Amir, capacity: 3);
        await grain.JoinAsync(Sara);
        await grain.JoinAsync(Vahid); // fills capacity, but still waits for Start (issue #104)
        Assert.True(await grain.StartAsync(Amir));

        var view = await grain.GetAsync(Amir);
        Assert.Equal((int)MatchState.InProgress, view!.State);
        Assert.Equal(3, view.Players.Count);
        Assert.Contains(view.Players, p => p.PlayerId == Vahid);

        // The view's own top-level roster -- LiveView.Participants, replacing the two-scalar
        // ChallengerId/OpponentId pair -- names every seat too, not just the first two. This is the
        // one place issue #52's own Participants-helper fix (see this class's own remarks) never
        // reached: it fed the round/player collections above off the helper already, but LiveView's
        // own constructor still spelled out ChallengerId/OpponentId by hand.
        Assert.Equal([Amir, Sara, Vahid], view.Participants);

        Advance(LiveRules.StartCountdown + TimeSpan.FromMilliseconds(50));
        var opened = await WaitForAsync(grain, Amir, v => v.Phase == (int)LivePhase.Question && v.RoundIndex == 0);

        // The round's own answer list is every seated player, not just the first two.
        Assert.Equal(3, opened.Rounds[0].Answers.Count);
        Assert.Contains(opened.Rounds[0].Answers, a => a.PlayerId == Vahid);
    }

    /// <summary>
    /// The other half of issue #53's gap: not just that <c>LiveView.Participants</c> names every
    /// seat while play is still going, but that <c>LiveEnded.Standings</c> ranks every seat once it
    /// is over. Amir answers every round correctly and Sara and Vahid never do, so the podium is
    /// unambiguous — Amir alone in first, Sara and Vahid tied for second — and specifically not a
    /// three-way draw, which is exactly the failure mode a global <c>IsDraw</c> would produce for a
    /// tie anywhere but the top.
    /// </summary>
    [Fact]
    public async Task A_three_player_duel_resolves_with_every_participant_and_their_standings_in_view_and_ended()
    {
        var (grain, id) = await NewLobbyAsync("LET-STAND3", Amir, capacity: 3);
        await grain.JoinAsync(Sara);
        await grain.JoinAsync(Vahid);
        Assert.True(await grain.StartAsync(Amir));

        Advance(LiveRules.StartCountdown + TimeSpan.FromMilliseconds(50));
        await WaitForAsync(grain, Amir, v => v.Phase == (int)LivePhase.Question && v.RoundIndex == 0);

        for (var slot = 0; slot < MatchRules.QuestionsPerMatch; slot++)
        {
            Assert.True(await grain.AnswerAsync(Amir, slot, 0));
            Assert.True(await grain.AnswerAsync(Sara, slot, 1));
            Assert.True(await grain.AnswerAsync(Vahid, slot, 1));

            await WaitForAsync(grain, Amir, v => v.Phase == (int)LivePhase.Reveal || v.State != (int)MatchState.InProgress);

            Advance(LiveRules.RevealTime + TimeSpan.FromMilliseconds(50));
            if (slot < MatchRules.QuestionsPerMatch - 1)
                await WaitForAsync(grain, Amir, v => v.Phase == (int)LivePhase.Question && v.RoundIndex == slot + 1);
        }

        var final = await WaitForAsync(grain, Amir, v => v.State == (int)MatchState.Resolved, timeoutMs: 10_000);
        Assert.Equal([Amir, Sara, Vahid], final.Participants);
        Assert.Equal(Amir, final.WinnerId);
        Assert.False(final.IsDraw); // Sara/Vahid sharing second is not a draw -- only a shared first place is

        await WaitForEventAsync(id, "Ended", 1);
        var ended = (LiveEnded)LiveShared.Notifier.EventsFor(id).Single(e => e.Kind == "Ended").Payload;
        Assert.Equal(Amir, ended.WinnerId);
        Assert.False(ended.IsDraw);

        Assert.Equal(3, ended.Standings.Count);
        var byId = ended.Standings.ToDictionary(s => s.PlayerId);
        Assert.Equal(1, byId[Amir].Place);
        Assert.Equal(MatchOutcome.Win, byId[Amir].Outcome);
        Assert.Equal(2, byId[Sara].Place);
        Assert.Equal(MatchOutcome.Loss, byId[Sara].Outcome);
        Assert.Equal(2, byId[Vahid].Place);
        Assert.Equal(MatchOutcome.Loss, byId[Vahid].Outcome);
    }

    [Fact]
    public async Task A_player_who_misses_three_rounds_in_a_row_is_eliminated_while_the_others_keep_playing()
    {
        var (grain, id) = await NewLobbyAsync("LET-ELIM1", Amir, capacity: 3);
        await grain.JoinAsync(Sara);
        await grain.JoinAsync(Vahid);
        Assert.True(await grain.StartAsync(Amir));

        Advance(LiveRules.StartCountdown + TimeSpan.FromMilliseconds(50));

        // Amir never answers, in any of the next three rounds. Sara and Vahid always do, so the
        // round can only ever close by the clock running out, not by the third answer arriving —
        // exactly the path CloseRound (and therefore its elimination) requires. Every jump below sets
        // the clock to an absolute instant derived from the deadline the grain itself just reported
        // (PhaseEndsAt), rather than adding a fixed delta on top of whatever it already was: the
        // latter accumulates the slack in each buffer round over round, and by the third round drifts
        // past the short Reveal window entirely, closing it and opening the next round in the same
        // jump before this ever observes Reveal.
        for (var slot = 0; slot < LiveRules.MissesBeforeAbandon; slot++)
        {
            var opened = await WaitForAsync(grain, Amir, v => v.Phase == (int)LivePhase.Question && v.RoundIndex == slot);
            Assert.True(await grain.AnswerAsync(Sara, slot, 0));
            Assert.True(await grain.AnswerAsync(Vahid, slot, 0));

            // Just past the question's own deadline plus its network grace — enough to close the
            // round by timeout, not so much that the same jump also overshoots the short Reveal
            // window and into the next round before this can observe it.
            LiveShared.TimeProvider.SetUtcNow(opened.PhaseEndsAt!.Value + MatchRules.NetworkGrace + TimeSpan.FromMilliseconds(500));
            var revealed = await WaitForAsync(grain, Amir, v => v.Phase == (int)LivePhase.Reveal);

            if (slot < LiveRules.MissesBeforeAbandon - 1)
                LiveShared.TimeProvider.SetUtcNow(revealed.PhaseEndsAt!.Value + TimeSpan.FromMilliseconds(50));
        }

        await WaitForEventAsync(id, "PlayerEliminated", 1);
        var elimination = (LivePlayerEliminated)LiveShared.Notifier.EventsFor(id).Single(e => e.Kind == "PlayerEliminated").Payload;
        Assert.Equal(Amir, elimination.PlayerId);
        Assert.Equal(LiveRules.MissesBeforeAbandon - 1, elimination.RoundSlot);

        // Eliminated, not ended: two of the three are still playing, so the duel carries on rather
        // than becoming a NoContest or an Abandoned finish.
        var view = await grain.GetAsync(Sara);
        Assert.Equal((int)MatchState.InProgress, view!.State);
        Assert.DoesNotContain(LiveShared.Notifier.EventsFor(id), e => e.Kind == "Ended");

        // The eliminated player can no longer answer -- refused, not silently ignored.
        Assert.False(await grain.AnswerAsync(Amir, LiveRules.MissesBeforeAbandon - 1, 0));
    }
}
