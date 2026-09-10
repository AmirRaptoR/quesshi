using Quesshi.Application.Ports;
using Quesshi.Application.UseCases;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;
using Quesshi.Server.Api;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

/// <summary>
/// Endpoint-level coverage for the lobby lifecycle <see cref="LiveEndpoints"/> gained in issue #52 —
/// create (with settings and capacity), leave, start, and owner-only settings — driven directly
/// against the internal static handlers, the same pattern <see cref="LiveEndpointsTests"/> already
/// uses for create/join/get/cancel. Join itself is not repeated here: it is the same
/// <c>POST /api/live/join/{code}</c> covered there, capacity-aware already.
/// </summary>
[Collection(nameof(LiveClusterCollection))]
public class LiveLobbyEndpointsTests(LiveClusterFixture fixture)
{
    private const string Amir = "lle-amir";
    private const string Sara = "lle-sara";
    private const string Vahid = "lle-vahid";
    private const string Stranger = "lle-stranger";
    private const string Category = "lle-scarce";

    private IGrainFactory Grains => fixture.Cluster.GrainFactory;
    private static readonly TimeProviderClock Clock = new(LiveShared.TimeProvider);
    private static readonly QuestionSetBuilder Builder = new(LiveShared.Questions, LiveShared.Categories);

    private static int _n = LiveIdRanges.LobbyEndpointsPoolStart;

    static LiveLobbyEndpointsTests()
    {
        LiveShared.Players.Items.Add(Player.Register(Amir, "lle-amir@example.com", "Amir", Language.En, LiveShared.TimeProvider.GetUtcNow()));
        LiveShared.Players.Items.Add(Player.Register(Sara, "lle-sara@example.com", "Sara", Language.En, LiveShared.TimeProvider.GetUtcNow()));
        LiveShared.Players.Items.Add(Player.Register(Vahid, "lle-vahid@example.com", "Vahid", Language.En, LiveShared.TimeProvider.GetUtcNow()));
        LiveShared.Players.Items.Add(Player.Register(Stranger, "lle-stranger@example.com", "Stranger", Language.En, LiveShared.TimeProvider.GetUtcNow()));

        if (LiveShared.Categories.Items.All(c => c.Id != Category))
            LiveShared.Categories.Items.Add(new Category(Category, "کمیاب", "Scarce", "globe", "#336699"));

        // 20, not just MatchRules.QuestionsPerMatch (10): UpdateSettings_by_the_owner_changes_the_question_count
        // draws a 20-question duel from this same pool.
        for (var i = 0; i < 20; i++)
        {
            var qid = $"lle-pool-q{i}";
            if (LiveShared.Questions.Items.Any(q => q.Id == qid)) continue;
            LiveShared.Questions.Items.Add(Question.Create(qid, Language.En, Category, MatchRules.LevelForSlot(i),
                $"pool question {i}", ["right", "wrong1", "wrong2", "wrong3"], 0, LiveShared.TimeProvider.GetUtcNow(),
                status: QuestionStatus.Approved));
        }
    }

    private static FakeIdFactory NewIds() => new(Interlocked.Add(ref _n, LiveIdRanges.NewIdsStep));

    private async Task<LiveViewDto> CreateLobbyAsync(FakeIdFactory ids, int capacity, string owner = Amir)
    {
        var result = await LiveEndpoints.CreateLobbyAsync(new CreateLobbyDto(capacity, "en", [Category], null, null),
            owner, Grains, ids, LiveShared.Archive, LiveShared.Players, LiveShared.Questions, LiveShared.Categories, Clock);
        Assert.Equal(200, CrossTypeCodeTests.StatusOf(result));
        return (LiveViewDto)CrossTypeCodeTests.ValueOf(result);
    }

    // ---- Create ----

    [Fact]
    public async Task Create_opens_an_N_player_lobby_with_no_questions_drawn_and_only_the_owner_seated()
    {
        var lobby = await CreateLobbyAsync(NewIds(), capacity: 3);

        Assert.Equal("awaitingopponent", lobby.State);
        Assert.Equal("lobby", lobby.Phase);
        Assert.Equal(0, lobby.TotalRounds); // nothing drawn until Start

        var view = await Grains.GetGrain<ILiveMatchGrain>(lobby.Id).GetAsync(Amir);
        Assert.Single(view!.Players);
    }

    [Fact]
    public async Task Create_refuses_a_capacity_outside_2_to_8()
    {
        var tooSmall = await LiveEndpoints.CreateLobbyAsync(new CreateLobbyDto(1, "en", [Category], null, null),
            Amir, Grains, NewIds(), LiveShared.Archive, LiveShared.Players, LiveShared.Questions, LiveShared.Categories, Clock);
        Assert.Equal(400, CrossTypeCodeTests.StatusOf(tooSmall));
        Assert.Equal("bad_capacity", CrossTypeCodeTests.ErrorOf(tooSmall));

        var tooBig = await LiveEndpoints.CreateLobbyAsync(new CreateLobbyDto(9, "en", [Category], null, null),
            Amir, Grains, NewIds(), LiveShared.Archive, LiveShared.Players, LiveShared.Questions, LiveShared.Categories, Clock);
        Assert.Equal(400, CrossTypeCodeTests.StatusOf(tooBig));
        Assert.Equal("bad_capacity", CrossTypeCodeTests.ErrorOf(tooBig));
    }

    [Fact]
    public async Task Create_coerces_an_out_of_range_question_count_to_the_default_rather_than_refusing()
    {
        var lobby = await LiveEndpoints.CreateLobbyAsync(new CreateLobbyDto(3, "en", [Category], 3, null),
            Amir, Grains, NewIds(), LiveShared.Archive, LiveShared.Players, LiveShared.Questions, LiveShared.Categories, Clock);
        Assert.Equal(200, CrossTypeCodeTests.StatusOf(lobby));

        // Start it to force the draw, then confirm the drawn set is the coerced default length.
        var dto = (LiveViewDto)CrossTypeCodeTests.ValueOf(lobby);
        await Grains.GetGrain<ILiveMatchGrain>(dto.Id).JoinAsync(Sara);
        Assert.True(await Grains.GetGrain<ILiveMatchGrain>(dto.Id).StartAsync(Amir));
        var view = await Grains.GetGrain<ILiveMatchGrain>(dto.Id).GetAsync(Amir);
        Assert.Equal(MatchRules.QuestionsPerMatch, view!.TotalRounds);
    }

    // ---- Leave ----

    [Fact]
    public async Task Leave_by_a_seated_non_owner_frees_their_seat()
    {
        var lobby = await CreateLobbyAsync(NewIds(), capacity: 3);
        await Grains.GetGrain<ILiveMatchGrain>(lobby.Id).JoinAsync(Sara);

        var ok = await Grains.GetGrain<ILiveMatchGrain>(lobby.Id).LeaveAsync(Sara);
        Assert.True(ok);

        var view = await Grains.GetGrain<ILiveMatchGrain>(lobby.Id).GetAsync(Amir);
        Assert.DoesNotContain(view!.Players, p => p.PlayerId == Sara);
        Assert.Equal((int)MatchState.AwaitingOpponent, view.State); // still open, room for someone else
    }

    [Fact]
    public async Task Leave_by_the_owner_ends_the_lobby_as_no_contest_rather_than_freeing_their_seat()
    {
        var lobby = await CreateLobbyAsync(NewIds(), capacity: 3);
        await Grains.GetGrain<ILiveMatchGrain>(lobby.Id).JoinAsync(Sara);

        var ok = await Grains.GetGrain<ILiveMatchGrain>(lobby.Id).LeaveAsync(Amir);
        Assert.True(ok);

        var view = await Grains.GetGrain<ILiveMatchGrain>(lobby.Id).GetAsync(Sara);
        Assert.Equal((int)MatchState.NoContest, view!.State);
    }

    [Fact]
    public async Task Leave_by_someone_never_seated_is_refused()
    {
        var lobby = await CreateLobbyAsync(NewIds(), capacity: 3);

        Assert.False(await Grains.GetGrain<ILiveMatchGrain>(lobby.Id).LeaveAsync(Stranger));
    }

    // ---- Start ----

    [Fact]
    public async Task Start_is_refused_below_two_seated_even_for_the_owner()
    {
        var lobby = await CreateLobbyAsync(NewIds(), capacity: 3);

        Assert.False(await Grains.GetGrain<ILiveMatchGrain>(lobby.Id).StartAsync(Amir));
    }

    [Fact]
    public async Task Start_is_refused_for_anyone_but_the_owner()
    {
        var lobby = await CreateLobbyAsync(NewIds(), capacity: 3);
        await Grains.GetGrain<ILiveMatchGrain>(lobby.Id).JoinAsync(Sara);

        Assert.False(await Grains.GetGrain<ILiveMatchGrain>(lobby.Id).StartAsync(Sara));

        var view = await Grains.GetGrain<ILiveMatchGrain>(lobby.Id).GetAsync(Amir);
        Assert.Equal((int)MatchState.AwaitingOpponent, view!.State); // untouched by the refused attempt
    }

    [Fact]
    public async Task Start_by_the_owner_with_room_to_spare_begins_the_duel_and_draws_the_question_set()
    {
        var lobby = await CreateLobbyAsync(NewIds(), capacity: 3);
        await Grains.GetGrain<ILiveMatchGrain>(lobby.Id).JoinAsync(Sara); // Vahid never shows up

        Assert.True(await Grains.GetGrain<ILiveMatchGrain>(lobby.Id).StartAsync(Amir));

        var view = await Grains.GetGrain<ILiveMatchGrain>(lobby.Id).GetAsync(Amir);
        Assert.Equal((int)MatchState.InProgress, view!.State);
        Assert.Equal(2, view.Players.Count);
        Assert.Equal(MatchRules.QuestionsPerMatch, view.TotalRounds);
    }

    // ---- Settings: owner-only ----

    [Fact]
    public async Task UpdateSettings_by_the_owner_changes_the_question_count()
    {
        var lobby = await CreateLobbyAsync(NewIds(), capacity: 3);

        // Language stays English: the pool this class seeds is English-only, and proving the update
        // took hold only needs one changed field, not a language this pool cannot actually draw from.
        var result = await LiveEndpoints.UpdateSettingsAsync(lobby.Id,
            new UpdateDuelSettingsDto("en", [Category], 20, null), Amir, Grains, LiveShared.Players);
        Assert.Equal(200, CrossTypeCodeTests.StatusOf(result));

        await Grains.GetGrain<ILiveMatchGrain>(lobby.Id).JoinAsync(Sara);
        await Grains.GetGrain<ILiveMatchGrain>(lobby.Id).StartAsync(Amir);
        var view = await Grains.GetGrain<ILiveMatchGrain>(lobby.Id).GetAsync(Amir);
        Assert.Equal(20, view!.TotalRounds);
    }

    [Fact]
    public async Task UpdateSettings_by_anyone_but_the_owner_is_refused_and_changes_nothing()
    {
        var lobby = await CreateLobbyAsync(NewIds(), capacity: 3);
        await Grains.GetGrain<ILiveMatchGrain>(lobby.Id).JoinAsync(Sara);

        var result = await LiveEndpoints.UpdateSettingsAsync(lobby.Id,
            new UpdateDuelSettingsDto("en", [Category], 20, null), Sara, Grains, LiveShared.Players);
        Assert.Equal(400, CrossTypeCodeTests.StatusOf(result));
        Assert.Equal("cannot_update_settings", CrossTypeCodeTests.ErrorOf(result));

        await Grains.GetGrain<ILiveMatchGrain>(lobby.Id).StartAsync(Amir);
        var view = await Grains.GetGrain<ILiveMatchGrain>(lobby.Id).GetAsync(Amir);
        Assert.Equal(MatchRules.QuestionsPerMatch, view!.TotalRounds); // Sara's attempt never took hold
    }

    [Fact]
    public async Task UpdateSettings_is_refused_once_the_question_set_is_drawn()
    {
        var lobby = await CreateLobbyAsync(NewIds(), capacity: 2);
        await Grains.GetGrain<ILiveMatchGrain>(lobby.Id).JoinAsync(Sara);
        await Grains.GetGrain<ILiveMatchGrain>(lobby.Id).StartAsync(Amir); // draws the set

        var result = await LiveEndpoints.UpdateSettingsAsync(lobby.Id,
            new UpdateDuelSettingsDto("nl", [Category], 20, null), Amir, Grains, LiveShared.Players);
        Assert.Equal(400, CrossTypeCodeTests.StatusOf(result));
    }
    [Fact]
    public async Task The_owner_can_reopen_the_lobby_they_just_created()
    {
        // The lobby page loads a lobby by joining the code it was handed, so this is the owner's own
        // route back into the room they made. It used to come back SelfJoin -- "you cannot join your
        // own challenge", true when a challenger waited for an opponent instead of holding a seat --
        // and the page rendered that as "that invite doesn't exist any more". An owner could create a
        // lobby, share the code, and never see the page they were meant to press Start on.
        var lobby = await CreateLobbyAsync(NewIds(), capacity: 4);

        var again = await Grains.GetGrain<ILiveMatchGrain>(lobby.Id).JoinAsync(Amir);

        Assert.Equal((int)LiveJoinResult.AlreadyIn, again);

        var view = await Grains.GetGrain<ILiveMatchGrain>(lobby.Id).GetAsync(Amir);
        Assert.Single(view!.Players);            // still one seat taken, not two
        Assert.Contains(Amir, view.Participants); // and it is still the owner's
    }

}
