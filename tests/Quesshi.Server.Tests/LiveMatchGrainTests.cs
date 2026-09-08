using System.Diagnostics;
using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;

namespace Quesshi.Server.Tests;

[Collection(nameof(LiveClusterCollection))]
public class LiveMatchGrainTests(LiveClusterFixture fixture)
{
    private const string Amir = "lp-amir";
    private const string Sara = "lp-sara";
    private const string Stranger = "lp-stranger";

    private ILiveMatchGrain NewGrain(out string id, out List<string> questionIds)
    {
        id = Guid.NewGuid().ToString("N");
        questionIds = SeedQuestions(id);
        return fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(id);
    }

    /// <summary>Ten questions where the correct answer is always index 0.</summary>
    private static List<string> SeedQuestions(string prefix)
    {
        if (LiveShared.Categories.Items.All(c => c.Id != "geography"))
            LiveShared.Categories.Items.Add(new Category("geography", "جغرافیا", "Geography", "globe", "#336699"));

        var ids = new List<string>();
        for (var slot = 0; slot < MatchRules.QuestionsPerMatch; slot++)
        {
            var qid = $"{prefix}-q{slot}";
            LiveShared.Questions.Items.Add(Question.Create(qid, Language.En, "geography", MatchRules.LevelForSlot(slot),
                $"question {slot}", ["right", "wrong1", "wrong2", "wrong3"], 0, LiveShared.TimeProvider.GetUtcNow(),
                explanation: "because", status: QuestionStatus.Approved));
            ids.Add(qid);
        }
        return ids;
    }

    private static void Advance(TimeSpan by) => LiveShared.TimeProvider.Advance(by);

    /// <summary>
    /// Grain timers fire off the fake clock asynchronously relative to the calling thread, so every
    /// clock-driven assertion polls on real wall-clock time until the grain's own processing catches
    /// up, rather than assuming <see cref="Advance"/> has already been applied when it returns.
    /// </summary>
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

    // ---- Dependency direction ----

    [Fact]
    public void Quesshi_grains_csproj_references_no_aspnet_or_signalr_package_and_no_server_project()
    {
        var csproj = ReadRepoFile("src", "Quesshi.Grains", "Quesshi.Grains.csproj");
        Assert.DoesNotContain("Microsoft.AspNetCore", csproj);
        Assert.DoesNotContain("SignalR", csproj);
        Assert.DoesNotContain("Quesshi.Server.csproj", csproj);
    }

    [Fact]
    public void Quesshi_application_csproj_gains_no_reference_to_grains_or_orleans()
    {
        var csproj = ReadRepoFile("src", "Quesshi.Application", "Quesshi.Application.csproj");
        Assert.DoesNotContain("Quesshi.Grains", csproj);
        Assert.DoesNotContain("Orleans", csproj);
    }

    [Fact]
    public void Quesshi_grains_abstractions_csproj_still_references_only_the_orleans_sdk()
    {
        var csproj = ReadRepoFile("src", "Quesshi.Grains.Abstractions", "Quesshi.Grains.Abstractions.csproj");
        var packageRefs = System.Text.RegularExpressions.Regex.Matches(csproj, "<PackageReference Include=\"([^\"]+)\"");
        Assert.Equal(["Microsoft.Orleans.Sdk"], packageRefs.Select(m => m.Groups[1].Value));
    }

    [Fact]
    public void No_live_duel_payload_or_view_carries_a_remaining_seconds_count()
    {
        Type[] types =
        [
            typeof(LiveView), typeof(LivePlayerView), typeof(LiveRoundResultView), typeof(LiveRoundAnswerView),
            typeof(LiveCountdown), typeof(LiveRoundCard), typeof(LiveRoundReveal), typeof(LivePlayerRound),
            typeof(LiveEnded), typeof(LivePlayerScore)
        ];
        foreach (var type in types)
            Assert.DoesNotContain(type.GetProperties(), p => p.Name.Contains("Seconds") || p.Name.Contains("Remaining"));
    }

    private static string ReadRepoFile(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Quesshi.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine([dir!.FullName, .. relativeParts]));
    }

    // ---- Create / Join ----

    [Fact]
    public async Task CreateAsync_is_idempotent_and_rejects_a_bad_question_list()
    {
        var grain = NewGrain(out _, out var questionIds);
        var first = await grain.CreateAsync("TESTCODE", (int)Language.En, Amir, questionIds);
        var second = await grain.CreateAsync("TESTCODE", (int)Language.En, "someone-else", questionIds);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(Amir, second.ChallengerId); // still the original challenger, not overwritten

        var freshGrain = NewGrain(out _, out var badList);
        badList.RemoveAt(0); // wrong count
        await Assert.ThrowsAsync<ArgumentException>(() => freshGrain.CreateAsync("TESTCODE", (int)Language.En, Amir, badList));
    }

    [Fact]
    public async Task JoinAsync_refuses_the_challenger_a_stranger_after_taken_and_is_idempotent_for_the_real_opponent()
    {
        var grain = NewGrain(out _, out var questionIds);
        await grain.CreateAsync("TESTCODE", (int)Language.En, Amir, questionIds);

        Assert.Equal((int)LiveJoinResult.SelfJoin, await grain.JoinAsync(Amir)); // cannot join your own challenge
        Assert.Equal((int)LiveJoinResult.Joined, await grain.JoinAsync(Sara));
        Assert.Equal((int)LiveJoinResult.Taken, await grain.JoinAsync(Stranger)); // already taken
        Assert.Equal((int)LiveJoinResult.AlreadyIn, await grain.JoinAsync(Sara)); // idempotent
    }

    [Fact]
    public async Task JoinAsync_refuses_a_lobby_that_has_already_expired()
    {
        var grain = NewGrain(out _, out var questionIds);
        await grain.CreateAsync("TESTCODE", (int)Language.En, Amir, questionIds);

        Advance(LiveRules.LobbyExpires + TimeSpan.FromSeconds(1));
        await WaitForAsync(grain, Amir, v => v.State == (int)MatchState.NoContest);

        Assert.Equal((int)LiveJoinResult.Expired, await grain.JoinAsync(Sara));
    }

    [Fact]
    public async Task CreateAsync_mirrors_the_duel_into_the_archive_so_its_code_can_be_resolved()
    {
        var grain = NewGrain(out var id, out var questionIds);
        var view = await grain.CreateAsync("MIRROR1", (int)Language.En, Amir, questionIds);

        var row = await LiveShared.Archive.ByCodeAsync("MIRROR1");
        Assert.NotNull(row);
        Assert.Equal(id, row!.Id);
        Assert.True(row.IsLive);
        Assert.Equal(Amir, row.ChallengerId);
        Assert.Equal(MatchState.AwaitingOpponent, row.State);
        Assert.Equal(view.Id, row.Id);
    }

    [Fact]
    public async Task JoinAsync_mirrors_the_opponent_into_the_archive()
    {
        var grain = NewGrain(out _, out var questionIds);
        await grain.CreateAsync("MIRROR2", (int)Language.En, Amir, questionIds);
        Assert.Equal((int)LiveJoinResult.Joined, await grain.JoinAsync(Sara));

        var row = await LiveShared.Archive.ByCodeAsync("MIRROR2");
        Assert.NotNull(row);
        Assert.Equal(Sara, row!.OpponentId);
        Assert.Equal(MatchState.InProgress, row.State);
    }

    [Fact]
    public async Task Expiry_mirrors_no_contest_into_the_archive_and_notifies_ended_exactly_once()
    {
        var grain = NewGrain(out var id, out var questionIds);
        await grain.CreateAsync("MIRROR3", (int)Language.En, Amir, questionIds);

        Advance(LiveRules.LobbyExpires + TimeSpan.FromSeconds(1));
        await WaitForAsync(grain, Amir, v => v.State == (int)MatchState.NoContest);
        await WaitForEventAsync(id, "Ended", 1);

        var row = await LiveShared.Archive.ByCodeAsync("MIRROR3");
        Assert.Equal(MatchState.NoContest, row!.State);
        Assert.Single(LiveShared.Notifier.EventsFor(id), e => e.Kind == "Ended");
    }

    // ---- Cancel ----

    [Fact]
    public async Task CancelAsync_by_the_challenger_in_the_lobby_ends_the_duel_and_notifies_once()
    {
        var grain = NewGrain(out var id, out var questionIds);
        await grain.CreateAsync("CANCEL1", (int)Language.En, Amir, questionIds);

        Assert.True(await grain.CancelAsync(Amir));

        var view = await grain.GetAsync(Amir);
        Assert.Equal((int)MatchState.NoContest, view!.State);
        await WaitForEventAsync(id, "Ended", 1);
        Assert.Single(LiveShared.Notifier.EventsFor(id), e => e.Kind == "Ended");
    }

    [Fact]
    public async Task CancelAsync_by_anyone_else_or_once_the_duel_has_left_the_lobby_is_refused()
    {
        var strangerGrain = NewGrain(out _, out var strangerIds);
        await strangerGrain.CreateAsync("CANCEL2", (int)Language.En, Amir, strangerIds);
        Assert.False(await strangerGrain.CancelAsync(Stranger));
        Assert.False(await strangerGrain.CancelAsync(Sara)); // not seated at all, still refused

        var joinedGrain = NewGrain(out _, out var joinedIds);
        await joinedGrain.CreateAsync("CANCEL3", (int)Language.En, Amir, joinedIds);
        await joinedGrain.JoinAsync(Sara);
        Assert.False(await joinedGrain.CancelAsync(Amir)); // no longer in the lobby

        var stillLobby = await strangerGrain.GetAsync(Amir);
        Assert.Equal((int)MatchState.AwaitingOpponent, stillLobby!.State);
    }

    [Fact]
    public async Task CreateAsync_registers_a_reminder_so_an_unjoined_lobby_expires_even_across_a_deactivation()
    {
        var grain = NewGrain(out var id, out var questionIds);
        await grain.CreateAsync("TESTCODE", (int)Language.En, Amir, questionIds);

        // Kill the activation — its in-memory timer dies with it. Only the reminder is left to
        // expire this lobby.
        await fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(id)
            .AsReference<Orleans.Core.Internal.IGrainManagementExtension>().DeactivateOnIdle();
        await Task.Delay(300);

        Advance(LiveRules.LobbyExpires + TimeSpan.FromSeconds(1));

        var view = await fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(id).GetAsync(Amir);
        Assert.NotNull(view);
        Assert.Equal((int)MatchState.NoContest, view!.State);
    }

    // ---- The clock ----

    [Fact]
    public async Task Joining_starts_the_countdown_and_it_opens_round_zero_on_schedule()
    {
        var grain = NewGrain(out _, out var questionIds);
        await grain.CreateAsync("TESTCODE", (int)Language.En, Amir, questionIds);
        await grain.JoinAsync(Sara);

        var afterJoin = await grain.GetAsync(Amir);
        Assert.Equal((int)LivePhase.Countdown, afterJoin!.Phase);

        Advance(LiveRules.StartCountdown + TimeSpan.FromMilliseconds(50));
        var opened = await WaitForAsync(grain, Amir, v => v.Phase == (int)LivePhase.Question);
        Assert.Equal(0, opened.RoundIndex);
    }

    [Fact]
    public async Task A_huge_silent_jump_from_the_countdown_finishes_no_contest_as_stale_without_simulating_any_round()
    {
        var grain = NewGrain(out var id, out var questionIds);
        await grain.CreateAsync("TESTCODE", (int)Language.En, Amir, questionIds);
        await grain.JoinAsync(Sara);

        // One big jump, entirely off the clock, from the countdown. LiveMatch's widened staleness
        // test (issue #49) now recognises a gap this size as an outage the instant Advance is
        // called and ends the duel right there — it no longer walks LiveMatch.Advance's
        // while (StepOnce(now)) loop through the countdown and three silent rounds
        // (LiveRules.MissesBeforeAbandon) to reach the same NoContest by simulation.
        var jump = LiveRules.StartCountdown
            + (MatchRules.QuestionTime + MatchRules.NetworkGrace + LiveRules.RevealTime) * (LiveRules.MissesBeforeAbandon + 1)
            + TimeSpan.FromSeconds(5);
        Advance(jump);

        var view = await WaitForAsync(grain, Amir, v => v.State == (int)MatchState.NoContest, timeoutMs: 10_000);
        Assert.Empty(view.Rounds); // nothing was ever simulated -- the gap was caught before round 0 opened
        Assert.Null(view.WinnerId);
    }

    [Fact]
    public async Task A_full_length_duel_resolves_driven_only_by_the_clock_between_answers()
    {
        var grain = NewGrain(out var id, out var questionIds);
        await grain.CreateAsync("TESTCODE", (int)Language.En, Amir, questionIds);
        await grain.JoinAsync(Sara);

        Advance(LiveRules.StartCountdown + TimeSpan.FromMilliseconds(50));
        await WaitForAsync(grain, Amir, v => v.Phase == (int)LivePhase.Question && v.RoundIndex == 0);

        for (var slot = 0; slot < MatchRules.QuestionsPerMatch; slot++)
        {
            // Amir always right, Sara always wrong: both answer, so the round closes immediately —
            // no need to wait for the buzzer.
            Assert.True(await grain.AnswerAsync(Amir, slot, 0));
            Assert.True(await grain.AnswerAsync(Sara, slot, 1));

            await WaitForAsync(grain, Amir, v => v.Phase == (int)LivePhase.Reveal || v.State != (int)MatchState.InProgress);

            Advance(LiveRules.RevealTime + TimeSpan.FromMilliseconds(50));
            if (slot < MatchRules.QuestionsPerMatch - 1)
                await WaitForAsync(grain, Amir, v => v.Phase == (int)LivePhase.Question && v.RoundIndex == slot + 1);
        }

        var final = await WaitForAsync(grain, Amir, v => v.State == (int)MatchState.Resolved, timeoutMs: 10_000);
        Assert.Equal(MatchRules.QuestionsPerMatch, final.Rounds.Count);
        Assert.Equal(Amir, final.WinnerId);
    }

    [Fact]
    public async Task The_second_answer_closes_the_round_early_and_the_original_deadline_timer_no_longer_fires_against_it()
    {
        var grain = NewGrain(out var id, out var questionIds);
        await grain.CreateAsync("TESTCODE", (int)Language.En, Amir, questionIds);
        await grain.JoinAsync(Sara);
        Advance(LiveRules.StartCountdown + TimeSpan.FromMilliseconds(50));
        await WaitForAsync(grain, Amir, v => v.Phase == (int)LivePhase.Question);

        var originalDeadline = MatchRules.QuestionTime;

        Assert.True(await grain.AnswerAsync(Amir, 0, 0));
        Assert.True(await grain.AnswerAsync(Sara, 0, 1)); // closes round 0 early -> Reveal, timer re-armed for the short reveal

        var revealed = await WaitForAsync(grain, Amir, v => v.Phase == (int)LivePhase.Reveal);
        Assert.Single(revealed.Rounds);

        // Cross what would have been the *original* question deadline (round 0 closed seconds ago,
        // long before it). If a stale timer for that deadline had survived instead of being
        // disposed and re-armed for the short reveal, this jump risks corrupting or skipping a
        // phase; the shortened schedule instead predicts exactly one round open — round 1 — by now.
        Advance(originalDeadline + TimeSpan.FromSeconds(1));
        var next = await WaitForAsync(grain, Amir, v => v.Phase == (int)LivePhase.Question && v.RoundIndex == 1);
        Assert.Equal(1, next.RoundIndex);
        Assert.Equal(2, next.Rounds.Count); // round 0 (closed) + round 1 (open) — not one phase further
    }

    [Fact]
    public async Task A_forced_deactivation_and_a_gap_longer_than_stale_after_recovers_as_no_contest()
    {
        var grain = NewGrain(out var id, out var questionIds);
        await grain.CreateAsync("TESTCODE", (int)Language.En, Amir, questionIds);
        await grain.JoinAsync(Sara);
        Advance(LiveRules.StartCountdown + TimeSpan.FromMilliseconds(50));
        await WaitForAsync(grain, Amir, v => v.Phase == (int)LivePhase.Question);

        await fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(id)
            .AsReference<Orleans.Core.Internal.IGrainManagementExtension>().DeactivateOnIdle();
        await Task.Delay(300);

        // Past LiveMatch.NextDueAt for a Question phase (the round's deadline plus NetworkGrace) by
        // more than StaleAfter — the widened staleness boundary (issue #49), not the round's raw
        // start time a smaller gap here used to be measured against.
        Advance(MatchRules.QuestionTime + MatchRules.NetworkGrace + LiveRules.StaleAfter + TimeSpan.FromSeconds(5));

        var recovered = await fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(id).GetAsync(Amir);
        Assert.NotNull(recovered);
        Assert.Equal((int)MatchState.NoContest, recovered!.State);
        Assert.Null(recovered.WinnerId);
    }

    [Fact]
    public async Task A_forced_deactivation_and_a_gap_shorter_than_stale_after_keeps_playing()
    {
        var grain = NewGrain(out var id, out var questionIds);
        await grain.CreateAsync("TESTCODE", (int)Language.En, Amir, questionIds);
        await grain.JoinAsync(Sara);
        Advance(LiveRules.StartCountdown + TimeSpan.FromMilliseconds(50));
        await WaitForAsync(grain, Amir, v => v.Phase == (int)LivePhase.Question);

        await fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(id)
            .AsReference<Orleans.Core.Internal.IGrainManagementExtension>().DeactivateOnIdle();
        await Task.Delay(300);

        Advance(TimeSpan.FromSeconds(5)); // short gap, well under LiveRules.StaleAfter

        var recovered = await fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(id).GetAsync(Amir);
        Assert.NotNull(recovered);
        Assert.NotEqual((int)MatchState.NoContest, recovered!.State);
        Assert.Equal((int)MatchState.InProgress, recovered.State);
    }

    [Fact]
    public async Task State_persists_to_hot_storage_and_rehydrates_after_deactivation()
    {
        var grain = NewGrain(out var id, out var questionIds);
        await grain.CreateAsync("TESTCODE", (int)Language.En, Amir, questionIds);
        await grain.JoinAsync(Sara);
        Advance(LiveRules.StartCountdown + TimeSpan.FromMilliseconds(50));
        await WaitForAsync(grain, Amir, v => v.Phase == (int)LivePhase.Question);

        Assert.True(await grain.AnswerAsync(Amir, 0, 0));

        await fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(id)
            .AsReference<Orleans.Core.Internal.IGrainManagementExtension>().DeactivateOnIdle();
        await Task.Delay(300);

        var rehydrated = await fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(id).GetAsync(Amir);
        Assert.NotNull(rehydrated);
        Assert.Equal(Sara, rehydrated!.OpponentId);
        Assert.True(rehydrated.Rounds.Single().Answers.Single(a => a.PlayerId == Amir).Answered);
    }

    // ---- Answering ----

    [Fact]
    public async Task AnswerAsync_resolves_the_question_and_records_served_and_correct_counters()
    {
        var grain = NewGrain(out _, out var questionIds);
        await grain.CreateAsync("TESTCODE", (int)Language.En, Amir, questionIds);
        await grain.JoinAsync(Sara);
        Advance(LiveRules.StartCountdown + TimeSpan.FromMilliseconds(50));
        await WaitForAsync(grain, Amir, v => v.Phase == (int)LivePhase.Question);

        var question = LiveShared.Questions.Items.Single(q => q.Id == questionIds[0]);
        var servedBefore = question.TimesServed;
        var correctBefore = question.TimesCorrect;

        Assert.True(await grain.AnswerAsync(Amir, 0, 0)); // correct

        var after = LiveShared.Questions.Items.Single(q => q.Id == questionIds[0]);
        Assert.Equal(servedBefore + 1, after.TimesServed);
        Assert.Equal(correctBefore + 1, after.TimesCorrect);
    }

    [Fact]
    public async Task AnswerAsync_refuses_a_late_answer_but_accepts_one_inside_the_grace_window()
    {
        var grain = NewGrain(out _, out var questionIds);
        await grain.CreateAsync("TESTCODE", (int)Language.En, Amir, questionIds);
        await grain.JoinAsync(Sara);
        Advance(LiveRules.StartCountdown + TimeSpan.FromMilliseconds(50));
        await WaitForAsync(grain, Amir, v => v.Phase == (int)LivePhase.Question);

        Advance(MatchRules.QuestionTime + TimeSpan.FromMilliseconds(500)); // inside NetworkGrace still
        Assert.True(await grain.AnswerAsync(Amir, 0, 0));

        var grain2 = NewGrain(out _, out var q2);
        await grain2.CreateAsync("TESTCODE2", (int)Language.En, Amir, q2);
        await grain2.JoinAsync(Sara);
        Advance(LiveRules.StartCountdown + TimeSpan.FromMilliseconds(50));
        await WaitForAsync(grain2, Amir, v => v.Phase == (int)LivePhase.Question);

        Advance(MatchRules.QuestionTime + MatchRules.NetworkGrace + TimeSpan.FromSeconds(5)); // past grace: closes the round
        // The jump may already have carried the duel past Reveal into round 1 by the time this
        // settles — either way, round 0 itself must be closed (its CorrectIndex populated).
        await WaitForAsync(grain2, Amir, v => v.Rounds.Count > 0 && v.Rounds[0].CorrectIndex is not null);
        Assert.False(await grain2.AnswerAsync(Amir, 0, 0));
    }

    [Fact]
    public async Task AnswerAsync_refuses_a_non_participant_a_wrong_slot_a_double_answer_and_a_finished_duel()
    {
        var grain = NewGrain(out var id, out var questionIds);
        await grain.CreateAsync("TESTCODE", (int)Language.En, Amir, questionIds);
        await grain.JoinAsync(Sara);
        Advance(LiveRules.StartCountdown + TimeSpan.FromMilliseconds(50));
        await WaitForAsync(grain, Amir, v => v.Phase == (int)LivePhase.Question);

        Assert.False(await grain.AnswerAsync(Stranger, 0, 0));
        Assert.False(await grain.AnswerAsync(Amir, 1, 0)); // round 0 is open, not round 1

        Assert.True(await grain.AnswerAsync(Amir, 0, 0));
        Assert.False(await grain.AnswerAsync(Amir, 0, 1)); // already answered

        await grain.EndAsync("test cleanup");
        Assert.False(await grain.AnswerAsync(Sara, 0, 0));
    }

    // ---- Redaction ----

    [Fact]
    public async Task LiveRoundCard_never_carries_the_correct_index()
    {
        var grain = NewGrain(out var id, out var questionIds);
        await grain.CreateAsync("TESTCODE", (int)Language.En, Amir, questionIds);
        await grain.JoinAsync(Sara);
        Advance(LiveRules.StartCountdown + TimeSpan.FromMilliseconds(50));
        await WaitForEventAsync(id, "RoundStarted", 1);

        var card = (LiveRoundCard)LiveShared.Notifier.EventsFor(id).Single(e => e.Kind == "RoundStarted").Payload;
        Assert.DoesNotContain(typeof(LiveRoundCard).GetProperties(), p => p.Name.Contains("Correct", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(questionIds[0], card.QuestionId);
        Assert.Equal(4, card.Choices.Count);
    }

    [Fact]
    public async Task GetAsync_redacts_the_open_round_but_reveals_a_closed_one()
    {
        var grain = NewGrain(out _, out var questionIds);
        await grain.CreateAsync("TESTCODE", (int)Language.En, Amir, questionIds);
        await grain.JoinAsync(Sara);
        Advance(LiveRules.StartCountdown + TimeSpan.FromMilliseconds(50));
        await WaitForAsync(grain, Amir, v => v.Phase == (int)LivePhase.Question);

        Assert.True(await grain.AnswerAsync(Amir, 0, 0));

        var saraSees = await grain.GetAsync(Sara);
        var openRound = saraSees!.Rounds.Single();
        Assert.Null(openRound.CorrectIndex);
        var amirAnswer = openRound.Answers.Single(a => a.PlayerId == Amir);
        Assert.True(amirAnswer.Answered);
        Assert.Null(amirAnswer.ChoiceIndex); // hidden from the opponent while the round is open
        Assert.Null(amirAnswer.Correct);

        var amirSees = await grain.GetAsync(Amir);
        var ownAnswer = amirSees!.Rounds.Single().Answers.Single(a => a.PlayerId == Amir);
        Assert.Equal(0, ownAnswer.ChoiceIndex); // a player always sees their own answer

        Assert.True(await grain.AnswerAsync(Sara, 0, 1)); // closes the round -> Reveal
        var afterReveal = await WaitForAsync(grain, Sara, v => v.Phase == (int)LivePhase.Reveal);
        var revealedRound = afterReveal.Rounds.Single();
        Assert.Equal(0, revealedRound.CorrectIndex);
        Assert.Equal(0, revealedRound.Answers.Single(a => a.PlayerId == Amir).ChoiceIndex);
        Assert.Equal(1, revealedRound.Answers.Single(a => a.PlayerId == Sara).ChoiceIndex);
    }

    [Fact]
    public async Task AnswerAsync_notifies_OpponentAnswered_once_for_the_first_answer_only()
    {
        var grain = NewGrain(out var id, out var questionIds);
        await grain.CreateAsync("TESTCODE", (int)Language.En, Amir, questionIds);
        await grain.JoinAsync(Sara);
        Advance(LiveRules.StartCountdown + TimeSpan.FromMilliseconds(50));
        await WaitForEventAsync(id, "RoundStarted", 1);

        Assert.True(await grain.AnswerAsync(Amir, 0, 0));
        await WaitForEventAsync(id, "OpponentAnswered", 1);
        var (slot, playerId) = ((int, string))LiveShared.Notifier.EventsFor(id).Single(e => e.Kind == "OpponentAnswered").Payload;
        Assert.Equal(0, slot);
        Assert.Equal(Amir, playerId);

        Assert.True(await grain.AnswerAsync(Sara, 0, 1)); // closes the round -> Reveal
        await WaitForEventAsync(id, "RoundRevealed", 1);
        Assert.Single(LiveShared.Notifier.EventsFor(id), e => e.Kind == "OpponentAnswered"); // still just the one
    }

    [Fact]
    public async Task GetAsync_returns_a_complete_view_for_a_participant_in_every_phase()
    {
        var grain = NewGrain(out _, out var questionIds);

        var lobby = await grain.CreateAsync("TESTCODE", (int)Language.En, Amir, questionIds);
        Assert.Equal((int)LivePhase.Lobby, lobby.Phase);
        Assert.NotNull(await grain.GetAsync(Amir));

        await grain.JoinAsync(Sara);
        var countdown = await grain.GetAsync(Amir);
        Assert.Equal((int)LivePhase.Countdown, countdown!.Phase);
        Assert.NotNull(countdown.PhaseEndsAt);

        Advance(LiveRules.StartCountdown + TimeSpan.FromMilliseconds(50));
        var question = await WaitForAsync(grain, Amir, v => v.Phase == (int)LivePhase.Question);
        Assert.Equal(0, question.RoundIndex);
        Assert.Equal(MatchRules.QuestionsPerMatch, question.TotalRounds);

        Assert.True(await grain.AnswerAsync(Amir, 0, 0));
        Assert.True(await grain.AnswerAsync(Sara, 0, 1));
        var reveal = await WaitForAsync(grain, Amir, v => v.Phase == (int)LivePhase.Reveal);
        Assert.Equal(0, reveal.Rounds.Single().CorrectIndex);

        await grain.EndAsync("end for the phase-coverage test");
        var over = await WaitForAsync(grain, Amir, v => v.State == (int)MatchState.NoContest);
        Assert.Equal((int)LivePhase.Over, over.Phase);
        Assert.Null(over.PhaseEndsAt);
    }

    [Fact]
    public async Task GetAsync_is_null_for_a_non_participant_and_for_a_duel_that_does_not_exist()
    {
        var grain = NewGrain(out _, out var questionIds);
        await grain.CreateAsync("TESTCODE", (int)Language.En, Amir, questionIds);
        await grain.JoinAsync(Sara);

        Assert.Null(await grain.GetAsync(Stranger));

        var missing = fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(Guid.NewGuid().ToString("N"));
        Assert.Null(await missing.GetAsync(Amir));
    }

    // ---- EndAsync ----

    [Fact]
    public async Task EndAsync_finishes_an_in_flight_duel_as_no_contest_and_is_a_no_op_afterwards()
    {
        var grain = NewGrain(out var id, out var questionIds);
        await grain.CreateAsync("TESTCODE", (int)Language.En, Amir, questionIds);
        await grain.JoinAsync(Sara);
        Advance(LiveRules.StartCountdown + TimeSpan.FromMilliseconds(50));
        await WaitForAsync(grain, Amir, v => v.Phase == (int)LivePhase.Question);

        await grain.EndAsync("admin kill");

        var view = await WaitForAsync(grain, Amir, v => v.State == (int)MatchState.NoContest);
        Assert.Null(view.WinnerId);

        var ended = LiveShared.Notifier.EventsFor(id).Where(e => e.Kind == "Ended").ToList();
        Assert.Single(ended);
        Assert.Equal("admin kill", ((LiveEnded)ended[0].Payload).Reason);

        await grain.EndAsync("second call should do nothing");
        Assert.Single(LiveShared.Notifier.EventsFor(id), e => e.Kind == "Ended");
    }

    [Fact]
    public async Task A_round_whose_question_cannot_be_resolved_ends_the_duel_as_no_contest_instead_of_wedging_it()
    {
        var id = Guid.NewGuid().ToString("N");
        var ghostIds = Enumerable.Range(0, MatchRules.QuestionsPerMatch).Select(i => $"{id}-ghost{i}").ToList();
        var grain = fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(id);
        await grain.CreateAsync("TESTCODE", (int)Language.En, Amir, ghostIds); // none of these ids exist in the question repository
        await grain.JoinAsync(Sara);

        Advance(LiveRules.StartCountdown + TimeSpan.FromMilliseconds(50));

        var view = await WaitForAsync(grain, Amir, v => v.State == (int)MatchState.NoContest);
        Assert.Null(view.WinnerId);
        await WaitForEventAsync(id, "Ended", 1);
        Assert.DoesNotContain(LiveShared.Notifier.EventsFor(id), e => e.Kind == "RoundStarted");
    }

    // ---- Notifications ----

    /// <summary>
    /// Drives a duel to a full, answered resolution — both players answering every round closes it
    /// immediately, exactly as <see cref="A_full_length_duel_resolves_driven_only_by_the_clock_between_answers"/>
    /// does — rather than by mutual silence: a jump big enough to reach
    /// <see cref="MatchState.NoContest"/> by silence alone is now caught by the widened staleness
    /// guard (issue #49) before a single round plays, which is exactly the point of that guard and
    /// not something this test is about.
    /// </summary>
    private async Task PlayFullDuelAsync(ILiveMatchGrain grain)
    {
        Advance(LiveRules.StartCountdown + TimeSpan.FromMilliseconds(50));
        await WaitForAsync(grain, Amir, v => v.Phase == (int)LivePhase.Question && v.RoundIndex == 0);

        for (var slot = 0; slot < MatchRules.QuestionsPerMatch; slot++)
        {
            Assert.True(await grain.AnswerAsync(Amir, slot, 0));
            Assert.True(await grain.AnswerAsync(Sara, slot, 1)); // closes the round immediately

            await WaitForAsync(grain, Amir, v => v.Phase == (int)LivePhase.Reveal || v.State != (int)MatchState.InProgress);
            Advance(LiveRules.RevealTime + TimeSpan.FromMilliseconds(50));
            if (slot < MatchRules.QuestionsPerMatch - 1)
                await WaitForAsync(grain, Amir, v => v.Phase == (int)LivePhase.Question && v.RoundIndex == slot + 1);
        }
    }

    [Fact]
    public async Task A_full_duel_notifies_in_exact_order_with_no_duplicates()
    {
        var grain = NewGrain(out var id, out var questionIds);
        await grain.CreateAsync("TESTCODE", (int)Language.En, Amir, questionIds);
        await grain.JoinAsync(Sara);

        await PlayFullDuelAsync(grain);
        await WaitForAsync(grain, Amir, v => v.State == (int)MatchState.Resolved, timeoutMs: 10_000);
        await WaitForEventAsync(id, "Ended", 1);

        var events = LiveShared.Notifier.EventsFor(id);
        var kinds = events.Select(e => e.Kind).ToList();
        var rounds = MatchRules.QuestionsPerMatch;

        // Every round: RoundStarted, then OpponentAnswered for the first of the two answers (both
        // players answer here, unlike the silence this test used to drive), then RoundRevealed.
        Assert.Equal("CountdownStarted", kinds[0]);
        for (var slot = 0; slot < rounds; slot++)
            Assert.Equal(["RoundStarted", "OpponentAnswered", "RoundRevealed"], kinds.Skip(1 + slot * 3).Take(3));
        Assert.Equal("Ended", kinds[^1]);
        Assert.Equal(1 + rounds * 3 + 1, kinds.Count);

        Assert.Equal(1, kinds.Count(k => k == "CountdownStarted"));
        Assert.Equal(rounds, kinds.Count(k => k == "RoundStarted"));
        Assert.Equal(rounds, kinds.Count(k => k == "OpponentAnswered"));
        Assert.Equal(rounds, kinds.Count(k => k == "RoundRevealed"));
        Assert.Equal(1, kinds.Count(k => k == "Ended"));
        Assert.DoesNotContain("OpponentPresenceChanged", kinds);
    }

    [Fact]
    public async Task A_throwing_notifier_does_not_corrupt_state_or_stall_the_duel()
    {
        LiveShared.Notifier.ThrowOnEveryCall = true;
        try
        {
            var grain = NewGrain(out var id, out var questionIds);
            await grain.CreateAsync("TESTCODE", (int)Language.En, Amir, questionIds);
            await grain.JoinAsync(Sara);

            await PlayFullDuelAsync(grain);

            var view = await WaitForAsync(grain, Amir, v => v.State == (int)MatchState.Resolved, timeoutMs: 10_000);
            Assert.Equal(MatchRules.QuestionsPerMatch, view.Rounds.Count);

            // The events were still recorded (the fake records before throwing) — the notifier
            // failing did not stop the duel from being driven to its normal end.
            Assert.NotEmpty(LiveShared.Notifier.EventsFor(id));
        }
        finally
        {
            LiveShared.Notifier.ThrowOnEveryCall = false;
        }
    }
}
