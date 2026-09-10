using Microsoft.AspNetCore.Http;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;
using Quesshi.Infrastructure;
using Quesshi.Server.Api;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

/// <summary>
/// The async twin of <see cref="LiveLobbyEndpointsTests"/>: the same lobby lifecycle
/// (<see cref="GameEndpoints.CreateLobbyAsync"/>, leave, start, owner-only settings) exists identically
/// for <see cref="IMatchGrain"/>, since step 3 (issue #51) gave both grain kinds the same lobby methods
/// together. A real <see cref="IdFactory"/>, not a seeded <see cref="FakeIdFactory"/>: unlike the live
/// cluster, nothing in this collection needs deterministic ids, so random ones avoid a collision with
/// zero bookkeeping.
/// </summary>
[Collection(nameof(ClusterCollection))]
public class AsyncLobbyEndpointsTests(ClusterFixture fixture)
{
    private const string Amir = "ale-amir";
    private const string Sara = "ale-sara";
    private const string Stranger = "ale-stranger";
    private const string Vahid = "ale-vahid";
    private const string Category = "ale-geography";

    private IGrainFactory Grains => fixture.Cluster.GrainFactory;
    private static readonly IdFactory Ids = new();

    static AsyncLobbyEndpointsTests()
    {
        Shared.Players.Items.Add(Player.Register(Amir, "ale-amir@example.com", "Amir", Language.En, Shared.Clock.Now));
        Shared.Players.Items.Add(Player.Register(Sara, "ale-sara@example.com", "Sara", Language.En, Shared.Clock.Now));
        Shared.Players.Items.Add(Player.Register(Stranger, "ale-stranger@example.com", "Stranger", Language.En, Shared.Clock.Now));
        Shared.Players.Items.Add(Player.Register(Vahid, "ale-vahid@example.com", "Vahid", Language.En, Shared.Clock.Now));

        if (Shared.Categories.Items.All(c => c.Id != Category))
            Shared.Categories.Items.Add(new Category(Category, "جغرافیا", "Geography", "globe", "#336699"));

        for (var i = 0; i < MatchRules.QuestionsPerMatch; i++)
        {
            var qid = $"ale-pool-q{i}";
            if (Shared.Questions.Items.Any(q => q.Id == qid)) continue;
            Shared.Questions.Items.Add(Question.Create(qid, Language.En, Category, MatchRules.LevelForSlot(i),
                $"pool question {i}", ["right", "wrong1", "wrong2", "wrong3"], 0, Shared.Clock.Now, status: QuestionStatus.Approved));
        }
    }

    private async Task<MatchSummaryDto> CreateLobbyAsync(int capacity, string owner = Amir)
    {
        var result = await GameEndpoints.CreateLobbyAsync(new CreateLobbyDto(capacity, "en", [Category], null, null), owner, Grains, Ids, Shared.Players);
        Assert.Equal(200, CrossTypeCodeTests.StatusOf(result));
        return (MatchSummaryDto)CrossTypeCodeTests.ValueOf(result);
    }

    [Fact]
    public async Task Create_opens_an_N_player_async_lobby_with_no_questions_drawn_and_only_the_owner_seated()
    {
        var lobby = await CreateLobbyAsync(capacity: 3);

        Assert.Equal("awaitingopponent", lobby.State);
        Assert.Equal(0, lobby.Questions);

        var view = await Grains.GetGrain<IMatchGrain>(lobby.Id).GetAsync(Amir);
        Assert.Equal((int)MatchState.AwaitingOpponent, view!.State);
        Assert.Empty(view.Runs); // seated, but nobody has called ServeNext yet
    }

    /// <summary>
    /// The async twin of <c>LiveEliminationTests</c>'s own "not just two" proof: <c>MatchGrain</c>'s
    /// <c>Sides</c> helper used to yield only <c>ChallengerId</c>/<c>OpponentId</c>, so a third-plus
    /// seat's run never reached <see cref="MatchView.Runs"/> no matter how many questions it served —
    /// the identical truncation issue #52 already fixed on the live side's own internal helper.
    /// </summary>
    [Fact]
    public async Task A_full_capacity_three_lobby_reports_every_participants_run_not_just_two()
    {
        var lobby = await CreateLobbyAsync(capacity: 3);
        var grain = Grains.GetGrain<IMatchGrain>(lobby.Id);
        await grain.JoinAsync(Sara);
        await grain.JoinAsync(Vahid); // fills capacity, but still waits for Start (issue #104)
        Assert.True(await grain.StartAsync(Amir)); // draws the question set

        // Every seated player takes their first turn, so each has a PlayerRun to report -- a run is
        // created lazily on first ServeNext, exactly like a capacity-2 match's opponent.
        Assert.NotNull(await grain.ServeNextAsync(Amir));
        Assert.NotNull(await grain.ServeNextAsync(Sara));
        Assert.NotNull(await grain.ServeNextAsync(Vahid));

        var view = await grain.GetAsync(Amir);
        Assert.Equal((int)MatchState.InProgress, view!.State);
        Assert.Equal(3, view.Runs.Count);
        Assert.Contains(view.Runs, r => r.PlayerId == Vahid);
    }

    /// <summary>
    /// The other half of the same truncation, one layer down: <c>MatchDoc.From</c> used to build the
    /// archive row's <c>Participants</c> array — the one field the multikey index and
    /// <c>MongoMatchArchive.ForPlayerAsync</c>'s <c>AnyEq</c> filter both query — off the two-scalar
    /// <c>ChallengerId</c>/<c>OpponentId</c> pair, not off <c>Results</c> (which already lists every
    /// real seat). A capacity&gt;2 lobby's third-and-later seats therefore had a real
    /// <see cref="ParticipantResult"/> sitting in a row nobody querying by their own id could ever
    /// find: fixed now, so Vahid's own "/api/matches" turns up a lobby he only ever joined, never
    /// created or "opponent"-ed into.
    /// </summary>
    [Fact]
    public async Task A_capacity_three_lobby_appears_in_the_third_players_own_matches_listing()
    {
        var lobby = await CreateLobbyAsync(capacity: 3);
        var grain = Grains.GetGrain<IMatchGrain>(lobby.Id);
        await grain.JoinAsync(Sara);
        await grain.JoinAsync(Vahid); // fills capacity, indexing the archive row again

        var list = await GameEndpoints.ListMatchesAsync(Vahid, activeOnly: false, take: null, Shared.Archive, Shared.Players, Grains);

        Assert.Contains(list, m => m.Id == lobby.Id);
    }

    [Fact]
    public async Task Create_refuses_a_capacity_outside_2_to_8()
    {
        var tooSmall = await GameEndpoints.CreateLobbyAsync(new CreateLobbyDto(1, "en", [Category], null, null), Amir, Grains, Ids, Shared.Players);
        Assert.Equal(400, CrossTypeCodeTests.StatusOf(tooSmall));
        Assert.Equal("bad_capacity", CrossTypeCodeTests.ErrorOf(tooSmall));

        var tooBig = await GameEndpoints.CreateLobbyAsync(new CreateLobbyDto(9, "en", [Category], null, null), Amir, Grains, Ids, Shared.Players);
        Assert.Equal(400, CrossTypeCodeTests.StatusOf(tooBig));
        Assert.Equal("bad_capacity", CrossTypeCodeTests.ErrorOf(tooBig));
    }

    [Fact]
    public async Task Leave_by_a_seated_non_owner_frees_their_seat()
    {
        var lobby = await CreateLobbyAsync(capacity: 3);
        await Grains.GetGrain<IMatchGrain>(lobby.Id).JoinAsync(Sara);

        Assert.True(await Grains.GetGrain<IMatchGrain>(lobby.Id).LeaveAsync(Sara));

        var view = await Grains.GetGrain<IMatchGrain>(lobby.Id).GetAsync(Amir);
        Assert.DoesNotContain(view!.Runs, r => r.PlayerId == Sara);
        Assert.Equal((int)MatchState.AwaitingOpponent, view.State);
    }

    [Fact]
    public async Task Leave_by_the_owner_ends_the_lobby_as_no_contest()
    {
        var lobby = await CreateLobbyAsync(capacity: 3);
        await Grains.GetGrain<IMatchGrain>(lobby.Id).JoinAsync(Sara);

        Assert.True(await Grains.GetGrain<IMatchGrain>(lobby.Id).LeaveAsync(Amir));

        var view = await Grains.GetGrain<IMatchGrain>(lobby.Id).GetAsync(Sara);
        Assert.Equal((int)MatchState.NoContest, view!.State);
    }

    [Fact]
    public async Task Start_is_refused_below_two_seated_and_for_anyone_but_the_owner()
    {
        var lobby = await CreateLobbyAsync(capacity: 3);
        Assert.False(await Grains.GetGrain<IMatchGrain>(lobby.Id).StartAsync(Amir)); // Amir alone so far

        await Grains.GetGrain<IMatchGrain>(lobby.Id).JoinAsync(Sara);
        Assert.False(await Grains.GetGrain<IMatchGrain>(lobby.Id).StartAsync(Sara)); // seated, not the owner

        Assert.True(await Grains.GetGrain<IMatchGrain>(lobby.Id).StartAsync(Amir));
        var view = await Grains.GetGrain<IMatchGrain>(lobby.Id).GetAsync(Amir);
        Assert.Equal((int)MatchState.InProgress, view!.State);
        Assert.Equal(MatchRules.QuestionsPerMatch, view.QuestionIds.Count);
    }

    [Fact]
    public async Task UpdateSettings_by_the_owner_changes_the_question_count_and_is_refused_for_anyone_else()
    {
        var lobby = await CreateLobbyAsync(capacity: 3);
        await Grains.GetGrain<IMatchGrain>(lobby.Id).JoinAsync(Sara);

        var refusedForSara = await GameEndpoints.UpdateSettingsAsync(lobby.Id,
            new UpdateDuelSettingsDto("en", [Category], 20, null), Sara, Grains, Shared.Players);
        Assert.Equal(400, CrossTypeCodeTests.StatusOf(refusedForSara));
        Assert.Equal("cannot_update_settings", CrossTypeCodeTests.ErrorOf(refusedForSara));

        var okForOwner = await GameEndpoints.UpdateSettingsAsync(lobby.Id,
            new UpdateDuelSettingsDto("en", [Category], MatchRules.QuestionsPerMatch, null), Amir, Grains, Shared.Players);
        Assert.Equal(200, CrossTypeCodeTests.StatusOf(okForOwner));

        await Grains.GetGrain<IMatchGrain>(lobby.Id).StartAsync(Amir);
        var view = await Grains.GetGrain<IMatchGrain>(lobby.Id).GetAsync(Amir);
        Assert.Equal(MatchRules.QuestionsPerMatch, view!.QuestionIds.Count); // Sara's refused attempt never took hold
    }

    [Fact]
    public async Task UpdateSettings_is_refused_once_the_question_set_is_drawn()
    {
        var lobby = await CreateLobbyAsync(capacity: 2);
        await Grains.GetGrain<IMatchGrain>(lobby.Id).JoinAsync(Sara);
        await Grains.GetGrain<IMatchGrain>(lobby.Id).StartAsync(Amir); // draws the set

        var result = await GameEndpoints.UpdateSettingsAsync(lobby.Id,
            new UpdateDuelSettingsDto("en", [Category], 20, null), Amir, Grains, Shared.Players);
        Assert.Equal(400, CrossTypeCodeTests.StatusOf(result));
    }

    [Fact]
    public async Task UpdateSettings_with_a_capacity_outside_2_to_8_is_refused_before_the_domain_sees_it()
    {
        var lobby = await CreateLobbyAsync(capacity: 3);

        var tooSmall = await GameEndpoints.UpdateSettingsAsync(lobby.Id,
            new UpdateDuelSettingsDto("en", [Category], null, null, 1), Amir, Grains, Shared.Players);
        Assert.Equal(400, CrossTypeCodeTests.StatusOf(tooSmall));
        Assert.Equal("bad_capacity", CrossTypeCodeTests.ErrorOf(tooSmall));

        var tooBig = await GameEndpoints.UpdateSettingsAsync(lobby.Id,
            new UpdateDuelSettingsDto("en", [Category], null, null, 9), Amir, Grains, Shared.Players);
        Assert.Equal(400, CrossTypeCodeTests.StatusOf(tooBig));
        Assert.Equal("bad_capacity", CrossTypeCodeTests.ErrorOf(tooBig));

        var view = await Grains.GetGrain<IMatchGrain>(lobby.Id).GetAsync(Amir);
        Assert.Equal(3, view!.Capacity); // neither attempt touched it
    }

    [Fact]
    public async Task UpdateSettings_with_a_valid_capacity_but_a_non_owner_caller_is_400_cannot_update_settings()
    {
        var lobby = await CreateLobbyAsync(capacity: 3);
        await Grains.GetGrain<IMatchGrain>(lobby.Id).JoinAsync(Sara);

        var result = await GameEndpoints.UpdateSettingsAsync(lobby.Id,
            new UpdateDuelSettingsDto("en", [Category], null, null, 4), Sara, Grains, Shared.Players);
        Assert.Equal(400, CrossTypeCodeTests.StatusOf(result));
        Assert.Equal("cannot_update_settings", CrossTypeCodeTests.ErrorOf(result));
    }

    [Fact]
    public async Task UpdateSettings_seats_three_of_four_after_the_owner_steps_capacity_from_two_to_four()
    {
        var lobby = await CreateLobbyAsync(capacity: 2);

        var result = await GameEndpoints.UpdateSettingsAsync(lobby.Id,
            new UpdateDuelSettingsDto("en", [Category], MatchRules.QuestionsPerMatch, null, 4), Amir, Grains, Shared.Players);
        Assert.Equal(200, CrossTypeCodeTests.StatusOf(result));

        await Grains.GetGrain<IMatchGrain>(lobby.Id).JoinAsync(Sara);
        await Grains.GetGrain<IMatchGrain>(lobby.Id).JoinAsync(Vahid);
        var view = await Grains.GetGrain<IMatchGrain>(lobby.Id).GetAsync(Amir);
        Assert.Equal(4, view!.Capacity);
        Assert.Equal(3, view.Participants.Count); // one open seat left
    }

    // ---- ByCodeAsync: the read path (issue #104) ----

    private async Task<IResult> ByCodeAsync(string code, string meId)
        => await GameEndpoints.ByCodeAsync(code, meId, Grains, Shared.Archive, Shared.Players);

    [Fact]
    public async Task ByCode_reads_an_open_lobby_for_a_non_participant_without_seating_them()
    {
        var lobby = await CreateLobbyAsync(capacity: 3);

        var result = await ByCodeAsync(lobby.Code, Sara);
        Assert.Equal(200, CrossTypeCodeTests.StatusOf(result));
        var dto = (MatchSummaryDto)CrossTypeCodeTests.ValueOf(result);
        Assert.Equal("awaitingopponent", dto.State);

        // Reading twice changes nothing -- no side effects at all.
        await ByCodeAsync(lobby.Code, Sara);
        var view = await Grains.GetGrain<IMatchGrain>(lobby.Id).GetAsync(Amir);
        Assert.DoesNotContain(view!.Runs, r => r.PlayerId == Sara);
    }

    [Fact]
    public async Task ByCode_is_200_for_a_participant_and_404_for_a_non_participant_once_the_match_has_started()
    {
        var lobby = await CreateLobbyAsync(capacity: 2);
        await Grains.GetGrain<IMatchGrain>(lobby.Id).JoinAsync(Sara);
        Assert.True(await Grains.GetGrain<IMatchGrain>(lobby.Id).StartAsync(Amir));

        var forOwner = await ByCodeAsync(lobby.Code, Amir);
        Assert.Equal(200, CrossTypeCodeTests.StatusOf(forOwner));

        var forStranger = await ByCodeAsync(lobby.Code, Stranger);
        Assert.Equal(404, CrossTypeCodeTests.StatusOf(forStranger));
        Assert.Equal("no_such_code", CrossTypeCodeTests.ErrorOf(forStranger));
    }

    [Fact]
    public async Task ByCode_on_an_unknown_code_is_404()
    {
        var result = await ByCodeAsync("NO-SUCH-CODE", Sara);
        Assert.Equal(404, CrossTypeCodeTests.StatusOf(result));
        Assert.Equal("no_such_code", CrossTypeCodeTests.ErrorOf(result));
    }
}
