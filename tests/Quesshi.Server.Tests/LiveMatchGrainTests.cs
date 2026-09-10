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
    private const string Vahid = "lp-vahid";

    private ILiveMatchGrain NewGrain(out string id, out List<string> questionIds)
    {
        id = Guid.NewGuid().ToString("N");
        questionIds = SeedQuestions(id);
        return fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(id);
    }

    /// <summary>A capacity-aware lobby, created through <see cref="ILiveMatchGrain.CreateLobbyAsync"/>
    /// with no question set drawn yet — seeds a fresh bank first, so a later auto-start or
    /// <c>StartAsync</c> always has enough questions to draw regardless of what other tests already
    /// pulled from the shared bank.</summary>
    private async Task<(ILiveMatchGrain Grain, string Id)> NewLobbyAsync(string code, string owner, int capacity)
    {
        var id = Guid.NewGuid().ToString("N");
        SeedQuestions(id);
        var grain = fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(id);
        await grain.CreateLobbyAsync(code, owner, (int)Language.En, MatchRules.QuestionsPerMatch, [], [], capacity);
        return (grain, id);
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
        Assert.Equal(Amir, second.Participants[0]); // still the original challenger, not overwritten

        var freshGrain = NewGrain(out _, out var badList);
        badList.RemoveAt(0); // wrong count
        await Assert.ThrowsAsync<ArgumentException>(() => freshGrain.CreateAsync("TESTCODE", (int)Language.En, Amir, badList));
    }

    [Fact]
    public async Task JoinAsync_refuses_the_challenger_a_stranger_after_full_and_is_idempotent_for_the_real_opponent()
    {
        var grain = NewGrain(out _, out var questionIds);
        await grain.CreateAsync("TESTCODE", (int)Language.En, Amir, questionIds);

        Assert.Equal((int)LiveJoinResult.AlreadyIn, await grain.JoinAsync(Amir)); // the owner holds seat 0, so joining is a no-op rather than a refusal
        Assert.Equal((int)LiveJoinResult.Joined, await grain.JoinAsync(Sara));
        // A capacity-2 lobby can only ever close by filling, so a latecomer sees Full, not Taken —
        // see LiveJoinResult's own remarks for the (capacity > 2) case Taken is still for.
        Assert.Equal((int)LiveJoinResult.Full, await grain.JoinAsync(Stranger));
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
        Assert.Equal(MatchState.AwaitingOpponent, row.State); // filling the seat no longer starts it
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
        Assert.True(await joinedGrain.StartAsync(Amir));
        Assert.False(await joinedGrain.CancelAsync(Amir)); // no longer in the lobby

        var stillLobby = await strangerGrain.GetAsync(Amir);
        Assert.Equal((int)MatchState.AwaitingOpponent, stillLobby!.State);
    }

    // ---- Lobby: capacity, Start, Leave, UpdateSettings (issue #51) ----

    [Fact]
    public async Task CreateLobbyAsync_opens_a_lobby_with_no_questions_drawn_and_only_the_owner_seated()
    {
        var (grain, _) = await NewLobbyAsync("LOBBY01", Amir, capacity: 3);

        var view = await grain.GetAsync(Amir);
        Assert.Equal((int)MatchState.AwaitingOpponent, view!.State);
        Assert.Equal((int)LivePhase.Lobby, view.Phase);
        Assert.Single(view.Players); // only the owner
        Assert.Equal(0, view.TotalRounds); // nothing drawn yet
    }

    [Fact]
    public async Task JoinAsync_seats_up_to_capacity_and_refuses_a_latecomer_as_full()
    {
        var (grain, _) = await NewLobbyAsync("LOBBY02", Amir, capacity: 3);

        Assert.Equal((int)LiveJoinResult.Joined, await grain.JoinAsync(Sara));
        Assert.Equal((int)LiveJoinResult.Joined, await grain.JoinAsync(Vahid)); // fills capacity, still waiting for Start
        Assert.Equal((int)LiveJoinResult.Full, await grain.JoinAsync(Stranger));
    }

    /// <summary>
    /// Two strangers racing for a two-seat lobby's one open seat (issue #104): Orleans serialises every
    /// call into a single grain activation's turn queue, so this proves the capacity guard added to
    /// <c>LiveMatch.Join</c> actually decides the race deterministically rather than both callers seeing
    /// a stale "one seat left" and both being seated.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_joins_for_the_last_seat_leave_exactly_one_seated()
    {
        var (grain, _) = await NewLobbyAsync("LOBBY11", Amir, capacity: 2);

        var results = await Task.WhenAll(grain.JoinAsync(Sara), grain.JoinAsync(Vahid));

        Assert.Single(results, (int)LiveJoinResult.Joined);
        Assert.Single(results, (int)LiveJoinResult.Full);

        var view = await grain.GetAsync(Amir);
        Assert.Equal(2, view!.Players.Count);
    }

    /// <summary>
    /// A capacity shrink racing a join for the seat that shrink would remove (issue #104): whichever of
    /// the two the single grain activation's turn queue serves first decides the other's outcome, but
    /// either order must leave a defined result — <c>Players.Count</c> never above whatever
    /// <c>Capacity</c> ends up being.
    /// </summary>
    [Fact]
    public async Task A_capacity_shrink_racing_a_join_never_leaves_more_players_than_the_final_capacity()
    {
        var (grain, _) = await NewLobbyAsync("LOBBY17", Amir, capacity: 3);
        await grain.JoinAsync(Sara); // 2 seated; capacity 3 has exactly one open seat

        var join = grain.JoinAsync(Vahid);
        var shrink = grain.UpdateSettingsAsync(Amir, (int)Language.En, MatchRules.QuestionsPerMatch, [], [], 2);
        await Task.WhenAll(join, shrink);

        var view = await grain.GetAsync(Amir);
        Assert.True(view!.Players.Count <= view.Capacity,
            $"Players.Count={view.Players.Count} exceeded Capacity={view.Capacity}");
    }

    [Fact]
    public async Task Reaching_capacity_no_longer_auto_starts_but_Start_draws_a_real_playable_question_set()
    {
        var (grain, _) = await NewLobbyAsync("LOBBY03", Amir, capacity: 3);

        await grain.JoinAsync(Sara);
        await grain.JoinAsync(Vahid); // the third seat fills capacity

        var afterFill = await grain.GetAsync(Amir);
        Assert.Equal((int)MatchState.AwaitingOpponent, afterFill!.State); // full, but still waiting for Start
        Assert.Equal((int)LivePhase.Lobby, afterFill.Phase);
        Assert.Equal(0, afterFill.TotalRounds); // nothing drawn until Start
        Assert.NotNull(await grain.GetAsync(Vahid)); // the third seat is a real, recognised participant

        Assert.True(await grain.StartAsync(Amir));
        var started = await grain.GetAsync(Amir);
        Assert.Equal((int)MatchState.InProgress, started!.State);
        Assert.Equal((int)LivePhase.Countdown, started.Phase);
        Assert.Equal(MatchRules.QuestionsPerMatch, started.TotalRounds); // Start drew the question set

        Advance(LiveRules.StartCountdown + TimeSpan.FromMilliseconds(50));
        var opened = await WaitForAsync(grain, Amir, v => v.Phase == (int)LivePhase.Question);
        Assert.Equal(0, opened.RoundIndex);
    }

    /// <summary>
    /// The core of issue #104: a two-seat lobby used to start the instant its second seat filled,
    /// indistinguishable from a bigger lobby's owner never getting to press Start. It no longer does.
    /// </summary>
    [Fact]
    public async Task A_capacity_two_lobby_stays_open_once_full_until_the_owner_presses_Start()
    {
        var (grain, _) = await NewLobbyAsync("LOBBY04", Amir, capacity: 2);

        Assert.Equal((int)LiveJoinResult.Joined, await grain.JoinAsync(Sara));

        var afterJoin = await grain.GetAsync(Amir);
        Assert.Equal((int)MatchState.AwaitingOpponent, afterJoin!.State);
        Assert.Equal((int)LivePhase.Lobby, afterJoin.Phase);
        Assert.Equal(0, afterJoin.TotalRounds);

        Assert.True(await grain.StartAsync(Amir));
        var started = await grain.GetAsync(Amir);
        Assert.Equal((int)MatchState.InProgress, started!.State);
        Assert.Equal((int)LivePhase.Countdown, started.Phase);
        Assert.Equal(MatchRules.QuestionsPerMatch, started.TotalRounds);
    }

    [Fact]
    public async Task StartAsync_is_owner_only_and_requires_two_seated()
    {
        var (grain, _) = await NewLobbyAsync("LOBBY05", Amir, capacity: 3);

        Assert.False(await grain.StartAsync(Amir)); // only the owner is seated so far

        await grain.JoinAsync(Sara);
        Assert.False(await grain.StartAsync(Sara)); // seated, but not the owner

        Assert.True(await grain.StartAsync(Amir));
        var view = await grain.GetAsync(Amir);
        Assert.Equal((int)MatchState.InProgress, view!.State);
        Assert.Equal(2, view.Players.Count); // Vahid never showed up -- room to spare is fine
        Assert.Equal(MatchRules.QuestionsPerMatch, view.TotalRounds); // Start drew the question set
    }

    [Fact]
    public async Task StartAsync_is_refused_once_the_lobby_has_already_started()
    {
        var (grain, _) = await NewLobbyAsync("LOBBY06", Amir, capacity: 2);
        await grain.JoinAsync(Sara);
        Assert.True(await grain.StartAsync(Amir));

        Assert.False(await grain.StartAsync(Amir)); // already started
    }

    [Fact]
    public async Task LeaveAsync_frees_a_seat_for_someone_else_to_take()
    {
        var (grain, _) = await NewLobbyAsync("LOBBY07", Amir, capacity: 3);
        await grain.JoinAsync(Sara); // one seat still open

        Assert.True(await grain.LeaveAsync(Sara));
        var afterLeave = await grain.GetAsync(Amir);
        Assert.Single(afterLeave!.Players); // only the owner remains
        Assert.Equal((int)MatchState.AwaitingOpponent, afterLeave.State);

        // The freed seat is really free: someone new can take it.
        Assert.Equal((int)LiveJoinResult.Joined, await grain.JoinAsync(Vahid));
    }

    [Fact]
    public async Task LeaveAsync_by_the_owner_ends_the_lobby_as_no_contest_with_no_ownership_transfer()
    {
        var (grain, id) = await NewLobbyAsync("LOBBY08", Amir, capacity: 3);
        await grain.JoinAsync(Sara);

        Assert.True(await grain.LeaveAsync(Amir));

        var view = await grain.GetAsync(Sara); // Sara is still a participant even though the duel is over
        Assert.Equal((int)MatchState.NoContest, view!.State);
        await WaitForEventAsync(id, "Ended", 1);
    }

    [Fact]
    public async Task LeaveAsync_is_refused_for_a_stranger_and_once_the_duel_has_started()
    {
        var (grain, _) = await NewLobbyAsync("LOBBY09", Amir, capacity: 2);
        Assert.False(await grain.LeaveAsync(Stranger)); // never seated

        await grain.JoinAsync(Sara);
        Assert.True(await grain.StartAsync(Amir));
        Assert.False(await grain.LeaveAsync(Sara)); // no longer in the lobby
    }

    [Fact]
    public async Task UpdateSettingsAsync_owner_only_and_refused_once_questions_are_drawn()
    {
        var (grain, id) = await NewLobbyAsync("LOBBY10", Amir, capacity: 3);
        SeedQuestions(id + "-extra"); // 20 total distinct En/geography questions, regardless of test order

        Assert.False(await grain.UpdateSettingsAsync(Sara, (int)Language.En, 20, [], [], null)); // not the owner
        Assert.True(await grain.UpdateSettingsAsync(Amir, (int)Language.En, 20, [], [], null));

        await grain.JoinAsync(Sara);
        await grain.JoinAsync(Vahid); // fills capacity, but no longer draws on its own (issue #104)
        Assert.True(await grain.StartAsync(Amir)); // Start draws 20 questions, per the updated settings

        var afterStart = await grain.GetAsync(Amir);
        Assert.Equal(20, afterStart!.TotalRounds);

        // Questions are drawn now, so settings can no longer change.
        Assert.False(await grain.UpdateSettingsAsync(Amir, (int)Language.En, MatchRules.QuestionsPerMatch, [], [], null));
    }

    /// <summary>
    /// The equal-settings no-op rule (issue #104): a lobby whose question set is already drawn while
    /// still in <see cref="LivePhase.Lobby"/> — exactly the shape <c>CreateAsync</c> builds for
    /// matchmaking's pairing path — can still have its capacity widened, because the settings half is
    /// attempted only when the requested settings actually differ from the lobby's own. Without this
    /// rule the settings half would always refuse (<c>QuestionIds</c> is non-empty) and a capacity-only
    /// PUT that resends the lobby's current settings could never succeed.
    /// </summary>
    [Fact]
    public async Task UpdateSettingsAsync_widens_capacity_alongside_the_lobbys_own_unchanged_settings_once_drawn()
    {
        var grain = NewGrain(out _, out var questionIds);
        await grain.CreateAsync("LOBBY12", (int)Language.En, Amir, questionIds); // pre-drawn, still Lobby phase

        var ok = await grain.UpdateSettingsAsync(Amir, (int)Language.En, questionIds.Count, [], [], 4);
        Assert.True(ok);

        var view = await grain.GetAsync(Amir);
        Assert.Equal(4, view!.Capacity);
        Assert.Equal((int)LivePhase.Lobby, view.Phase); // untouched otherwise
    }

    /// <summary>
    /// Atomic apply-both-or-neither (issue #104): a capacity half that fails (below the seated count)
    /// must leave the settings half unapplied too, even though the settings half alone would have
    /// succeeded.
    /// </summary>
    [Fact]
    public async Task UpdateSettingsAsync_refuses_both_halves_when_the_capacity_half_alone_would_fail()
    {
        var (grain, id) = await NewLobbyAsync("LOBBY13", Amir, capacity: 3);
        SeedQuestions(id + "-extra");
        await grain.JoinAsync(Sara);
        await grain.JoinAsync(Vahid); // 3 seated

        var ok = await grain.UpdateSettingsAsync(Amir, (int)Language.En, 20, [], [], 2); // 2 < 3 seated
        Assert.False(ok);

        var view = await grain.GetAsync(Amir);
        Assert.Equal(3, view!.Capacity); // unchanged

        Assert.True(await grain.StartAsync(Amir));
        var afterStart = await grain.GetAsync(Amir);
        Assert.Equal(MatchRules.QuestionsPerMatch, afterStart!.TotalRounds); // settings half was never applied either
    }

    [Fact]
    public async Task UpdateSettingsAsync_omitting_capacity_leaves_it_unchanged()
    {
        var (grain, _) = await NewLobbyAsync("LOBBY14", Amir, capacity: 3);

        Assert.True(await grain.UpdateSettingsAsync(Amir, (int)Language.En, MatchRules.QuestionsPerMatch, [], [], null));

        var view = await grain.GetAsync(Amir);
        Assert.Equal(3, view!.Capacity);
    }

    [Fact]
    public async Task UpdateSettingsAsync_is_refused_after_expiry_and_leaves_the_lobby_correctly_expired()
    {
        var (grain, id) = await NewLobbyAsync("LOBBY15", Amir, capacity: 2);
        Advance(LiveRules.LobbyExpires + TimeSpan.FromSeconds(1));
        await WaitForAsync(grain, Amir, v => v.State == (int)MatchState.NoContest);

        var ok = await grain.UpdateSettingsAsync(Amir, (int)Language.En, MatchRules.QuestionsPerMatch, [], [], 4);
        Assert.False(ok);

        var view = await grain.GetAsync(Amir);
        Assert.Equal((int)MatchState.NoContest, view!.State); // still correctly expired, not resurrected
        var row = await LiveShared.Archive.ByCodeAsync("LOBBY15");
        Assert.Equal(MatchState.NoContest, row!.State); // and persisted
    }

    [Fact]
    public async Task A_capacity_change_survives_deactivation_and_reactivation()
    {
        var (grain, id) = await NewLobbyAsync("LOBBY16", Amir, capacity: 2);
        Assert.True(await grain.UpdateSettingsAsync(Amir, (int)Language.En, MatchRules.QuestionsPerMatch, [], [], 5));

        await fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(id)
            .AsReference<Orleans.Core.Internal.IGrainManagementExtension>().DeactivateOnIdle();
        await Task.Delay(300);

        var view = await fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(id).GetAsync(Amir);
        Assert.Equal(5, view!.Capacity);
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
    public async Task Starting_the_lobby_begins_the_countdown_and_it_opens_round_zero_on_schedule()
    {
        var grain = NewGrain(out _, out var questionIds);
        await grain.CreateAsync("TESTCODE", (int)Language.En, Amir, questionIds);
        await grain.JoinAsync(Sara);
        Assert.True(await grain.StartAsync(Amir));

        var afterStart = await grain.GetAsync(Amir);
        Assert.Equal((int)LivePhase.Countdown, afterStart!.Phase);

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
        Assert.True(await grain.StartAsync(Amir));

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
        Assert.True(await grain.StartAsync(Amir));

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
        Assert.True(await grain.StartAsync(Amir));
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
        Assert.True(await grain.StartAsync(Amir));
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
        Assert.True(await grain.StartAsync(Amir));
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
        Assert.True(await grain.StartAsync(Amir));
        Advance(LiveRules.StartCountdown + TimeSpan.FromMilliseconds(50));
        await WaitForAsync(grain, Amir, v => v.Phase == (int)LivePhase.Question);

        Assert.True(await grain.AnswerAsync(Amir, 0, 0));

        await fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(id)
            .AsReference<Orleans.Core.Internal.IGrainManagementExtension>().DeactivateOnIdle();
        await Task.Delay(300);

        var rehydrated = await fixture.Cluster.GrainFactory.GetGrain<ILiveMatchGrain>(id).GetAsync(Amir);
        Assert.NotNull(rehydrated);
        Assert.Equal(Sara, rehydrated!.Participants[1]);
        Assert.True(rehydrated.Rounds.Single().Answers.Single(a => a.PlayerId == Amir).Answered);
    }

    // ---- Answering ----

    [Fact]
    public async Task AnswerAsync_resolves_the_question_and_records_served_and_correct_counters()
    {
        var grain = NewGrain(out _, out var questionIds);
        await grain.CreateAsync("TESTCODE", (int)Language.En, Amir, questionIds);
        await grain.JoinAsync(Sara);
        Assert.True(await grain.StartAsync(Amir));
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
        Assert.True(await grain.StartAsync(Amir));
        Advance(LiveRules.StartCountdown + TimeSpan.FromMilliseconds(50));
        await WaitForAsync(grain, Amir, v => v.Phase == (int)LivePhase.Question);

        Advance(MatchRules.QuestionTime + TimeSpan.FromMilliseconds(500)); // inside NetworkGrace still
        Assert.True(await grain.AnswerAsync(Amir, 0, 0));

        var grain2 = NewGrain(out _, out var q2);
        await grain2.CreateAsync("TESTCODE2", (int)Language.En, Amir, q2);
        await grain2.JoinAsync(Sara);
        Assert.True(await grain2.StartAsync(Amir));
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
        Assert.True(await grain.StartAsync(Amir));
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
        Assert.True(await grain.StartAsync(Amir));
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
        Assert.True(await grain.StartAsync(Amir));
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
        Assert.True(await grain.StartAsync(Amir));
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
        var stillLobby = await grain.GetAsync(Amir);
        Assert.Equal((int)LivePhase.Lobby, stillLobby!.Phase); // filling the seat no longer starts it

        Assert.True(await grain.StartAsync(Amir));
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
        Assert.True(await grain.StartAsync(Amir));
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
        Assert.True(await grain.StartAsync(Amir));

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
        Assert.True(await grain.StartAsync(Amir));

        await PlayFullDuelAsync(grain);
        await WaitForAsync(grain, Amir, v => v.State == (int)MatchState.Resolved, timeoutMs: 10_000);
        await WaitForEventAsync(id, "Ended", 1);

        var events = LiveShared.Notifier.EventsFor(id);
        var kinds = events.Select(e => e.Kind).ToList();
        var rounds = MatchRules.QuestionsPerMatch;

        // Every round: RoundStarted, then OpponentAnswered for the first of the two answers (both
        // players answer here, unlike the silence this test used to drive), then RoundRevealed.
        // Issue #104 removed Join's auto-start: Sara's join fires its own LobbyUpdated (JoinAsync's
        // explicit push for a real new seat), and only Amir's later StartAsync fires CountdownStarted
        // (the phase transition it causes) followed by its own explicit LobbyUpdated push.
        Assert.Equal("LobbyUpdated", kinds[0]);
        Assert.Equal("CountdownStarted", kinds[1]);
        Assert.Equal("LobbyUpdated", kinds[2]);
        for (var slot = 0; slot < rounds; slot++)
            Assert.Equal(["RoundStarted", "OpponentAnswered", "RoundRevealed"], kinds.Skip(3 + slot * 3).Take(3));
        Assert.Equal("Ended", kinds[^1]);
        Assert.Equal(3 + rounds * 3 + 1, kinds.Count);

        Assert.Equal(2, kinds.Count(k => k == "LobbyUpdated"));
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
            Assert.True(await grain.StartAsync(Amir));

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
