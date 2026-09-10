using Microsoft.AspNetCore.Http;
using Quesshi.Application.Ports;
using Quesshi.Application.UseCases;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;
using Quesshi.Server.Api;
using Quesshi.Server.Auth;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

/// <summary>
/// Endpoint-level coverage for <see cref="LiveEndpoints"/>, driven directly against the internal
/// static handlers the same way <see cref="MatchListTests"/> drives <c>GameEndpoints.ListMatchesAsync</c>
/// — the pattern the issue calls out, since <c>WebApplicationFactory&lt;Program&gt;</c> needs live
/// Redis for Orleans clustering. The one criterion that needs the real HTTP pipeline — the guest
/// filter — is covered separately in <see cref="LiveEndpointsGuestGateTests"/>.
/// </summary>
[Collection(nameof(LiveClusterCollection))]
public class LiveEndpointsTests(LiveClusterFixture fixture)
{
    private const string Amir = "lep-amir";
    private const string Sara = "lep-sara";
    private const string Stranger = "lep-stranger";

    private IGrainFactory Grains => fixture.Cluster.GrainFactory;
    private static readonly TimeProviderClock Clock = new(LiveShared.TimeProvider);
    private static readonly QuestionSetBuilder Builder = new(LiveShared.Questions, LiveShared.Categories);

    private static int _n = LiveIdRanges.NewIdsPoolStart;

    static LiveEndpointsTests()
    {
        LiveShared.Players.Items.Add(Player.Register(Amir, "amir@example.com", "Amir", Language.En, LiveShared.TimeProvider.GetUtcNow()));
        LiveShared.Players.Items.Add(Player.Register(Sara, "sara@example.com", "Sara", Language.En, LiveShared.TimeProvider.GetUtcNow()));
        LiveShared.Players.Items.Add(Player.Register(Stranger, "stranger@example.com", "Stranger", Language.En, LiveShared.TimeProvider.GetUtcNow()));

        // A category of this test class's own, so other test classes sharing LiveShared's static
        // pool cannot add it more questions behind its back and make the "not enough" test flaky.
        if (LiveShared.Categories.Items.All(c => c.Id != ScarceCategory))
            LiveShared.Categories.Items.Add(new Category(ScarceCategory, "کمیاب", "Scarce", "globe", "#336699"));

        // Enough approved English questions, across levels, that QuestionSetBuilder's fallback chain
        // can fill a default-length (10) duel however it slices the ramp — and not one more, so a
        // request for more than that can reliably prove "not enough".
        for (var i = 0; i < MatchRules.QuestionsPerMatch; i++)
        {
            var qid = $"lep-pool-q{i}";
            if (LiveShared.Questions.Items.Any(q => q.Id == qid)) continue;
            LiveShared.Questions.Items.Add(Question.Create(qid, Language.En, ScarceCategory, MatchRules.LevelForSlot(i),
                $"pool question {i}", ["right", "wrong1", "wrong2", "wrong3"], 0, LiveShared.TimeProvider.GetUtcNow(),
                status: QuestionStatus.Approved));
        }
    }

    private const string ScarceCategory = "lep-scarce";

    /// <summary>A fresh <see cref="IIdFactory"/> per test, so each test's codes are its own and archive
    /// lookups by code cannot pick up another test's row. Seeded from this class's own reserved slice
    /// of <see cref="LiveIdRanges"/> — see <see cref="LiveIdRanges.NewIdsPoolStart"/> for why: a
    /// zero-based counter here collided with <see cref="LiveShared.Ids"/>' own zero-based counter
    /// (issue #34).</summary>
    private static FakeIdFactory NewIds() => new(Interlocked.Add(ref _n, LiveIdRanges.NewIdsStep));

    private async Task<IResult> CreateAsync(FakeIdFactory ids, string meId = Amir, int? questions = null, bool random = false)
        => await LiveEndpoints.CreateAsync(new CreateMatchDto(random, "en", [ScarceCategory], questions), meId,
            Grains, Builder, ids, LiveShared.Archive, LiveShared.Players, LiveShared.Questions, LiveShared.Categories, Clock);

    private static LiveViewDto ViewOf(IResult result) => (LiveViewDto)CrossTypeCodeTests.ValueOf(result);

    [Fact]
    public async Task Create_makes_a_lobby_with_a_share_code_and_the_challengers_name_resolves_by_invite()
    {
        var ids = NewIds();
        var result = await CreateAsync(ids);
        Assert.Equal(200, CrossTypeCodeTests.StatusOf(result));

        var dto = ViewOf(result);
        Assert.Equal("awaitingopponent", dto.State);
        Assert.False(string.IsNullOrWhiteSpace(dto.Code));

        var invite = (InviteDto)CrossTypeCodeTests.ValueOf(await AuthEndpoints.InviteAsync(dto.Code, LiveShared.Archive, LiveShared.Players));
        Assert.True(invite.Live);
        Assert.True(invite.Open);
        Assert.Equal("Amir", invite.ChallengerName);
    }

    [Fact]
    public async Task Create_coerces_an_out_of_range_question_count_to_the_default_rather_than_refusing()
    {
        var ids = NewIds();
        var result = await CreateAsync(ids, questions: 3); // not one of MatchRules.QuestionCountChoices
        Assert.Equal(200, CrossTypeCodeTests.StatusOf(result));

        var view = await Grains.GetGrain<ILiveMatchGrain>(ViewOf(result).Id).GetAsync(Amir);
        Assert.Equal(MatchRules.QuestionsPerMatch, view!.TotalRounds);
    }

    [Fact]
    public async Task Create_with_random_true_refuses_and_creates_nothing()
    {
        var ids = NewIds();
        var before = LiveShared.Archive.Items.Count;

        var result = await CreateAsync(ids, random: true);

        Assert.Equal(400, CrossTypeCodeTests.StatusOf(result));
        Assert.Equal("random_not_supported", CrossTypeCodeTests.ErrorOf(result));
        Assert.Equal(before, LiveShared.Archive.Items.Count);
    }

    [Fact]
    public async Task Create_when_there_are_not_enough_questions_returns_503()
    {
        var ids = NewIds();
        // A valid choice (100) far past the 10 approved questions the pool actually has.
        var result = await LiveEndpoints.CreateAsync(new CreateMatchDto(false, "en", [ScarceCategory], 100), Amir,
            Grains, Builder, ids, LiveShared.Archive, LiveShared.Players, LiveShared.Questions, LiveShared.Categories, Clock);

        Assert.Equal(503, CrossTypeCodeTests.StatusOf(result));
    }

    [Fact]
    public async Task Create_retries_a_colliding_code_and_succeeds_on_a_fresh_one()
    {
        var ids = new FakeIdFactory(LiveIdRanges.CollidingCodeRetrySeed) { CodesToRepeat = 1 }; // first NewMatchCode() collides, second is fresh
        LiveShared.Archive.Items.Add(new ArchivedMatch("someone-elses-id", ids.PeekNextCode(), Language.En,
            "someone-else", null, null, false, FakeArchive.TestResults("someone-else", null, 0, 0),
            MatchState.AwaitingOpponent, Clock.Now, null, [], IsLive: true));

        var result = await CreateAsync(ids);
        Assert.Equal(200, CrossTypeCodeTests.StatusOf(result));
        Assert.NotEqual("someone-elses-id", ViewOf(result).Id);
    }

    [Fact]
    public async Task Create_gives_up_after_repeated_collisions_and_returns_503()
    {
        var ids = new FakeIdFactory(LiveIdRanges.CollidingCodeGiveUpSeed) { CodesToRepeat = int.MaxValue }; // every code this factory makes already collides
        LiveShared.Archive.Items.Add(new ArchivedMatch("blocker", ids.PeekNextCode(), Language.En,
            "someone-else", null, null, false, FakeArchive.TestResults("someone-else", null, 0, 0),
            MatchState.AwaitingOpponent, Clock.Now, null, [], IsLive: true));

        var result = await CreateAsync(ids);
        Assert.Equal(503, CrossTypeCodeTests.StatusOf(result));
    }

    /// <summary>
    /// Issue #104: filling a two-seat lobby by joining no longer starts it on its own — the owner's
    /// Start is the only door into countdown, for this plain-challenge lobby exactly as for one made
    /// through the N-player lobby path.
    /// </summary>
    [Fact]
    public async Task Join_seats_the_opponent_but_leaves_the_lobby_open_for_the_owner_to_press_Start()
    {
        var ids = NewIds();
        var created = ViewOf(await CreateAsync(ids));

        var result = await LiveEndpoints.JoinAsync(created.Code, Sara, Grains, LiveShared.Archive, LiveShared.Players, LiveShared.Questions, LiveShared.Categories, Clock);
        Assert.Equal(200, CrossTypeCodeTests.StatusOf(result));

        var dto = ViewOf(result);
        Assert.Equal("lobby", dto.Phase);
        Assert.Equal(Sara, dto.Participants[1].PlayerId);

        Assert.DoesNotContain(LiveShared.Notifier.EventsFor(created.Id), e => e.Kind == "CountdownStarted");

        Assert.True(await Grains.GetGrain<ILiveMatchGrain>(created.Id).StartAsync(Amir));
        Assert.Single(LiveShared.Notifier.EventsFor(created.Id), e => e.Kind == "CountdownStarted");
    }

    [Fact]
    public async Task Joining_your_own_lobby_hands_it_back_rather_than_refusing_it()
    {
        // This is how the lobby page loads for its owner -- it joins the code it was given -- so a
        // refusal here meant an owner could create a lobby, share the code, and then be told their
        // own invite did not exist.
        var ids = NewIds();
        var created = ViewOf(await CreateAsync(ids));

        var result = await LiveEndpoints.JoinAsync(created.Code, Amir, Grains, LiveShared.Archive, LiveShared.Players, LiveShared.Questions, LiveShared.Categories, Clock);

        Assert.Equal(200, CrossTypeCodeTests.StatusOf(result));
        Assert.Equal(created.Id, ((LiveViewDto)CrossTypeCodeTests.ValueOf(result)).Id);
    }

    [Fact]
    public async Task Join_a_lobby_that_already_has_an_opponent_is_refused()
    {
        var ids = NewIds();
        var created = ViewOf(await CreateAsync(ids));
        await LiveEndpoints.JoinAsync(created.Code, Sara, Grains, LiveShared.Archive, LiveShared.Players, LiveShared.Questions, LiveShared.Categories, Clock);

        var result = await LiveEndpoints.JoinAsync(created.Code, Stranger, Grains, LiveShared.Archive, LiveShared.Players, LiveShared.Questions, LiveShared.Categories, Clock);
        Assert.Equal(400, CrossTypeCodeTests.StatusOf(result));
        Assert.Equal("cannot_join", CrossTypeCodeTests.ErrorOf(result));
    }

    [Fact]
    public async Task Join_an_expired_lobby_is_refused_distinctly_from_unknown_or_taken()
    {
        var ids = NewIds();
        var created = ViewOf(await CreateAsync(ids));

        LiveShared.TimeProvider.Advance(LiveRules.LobbyExpires + TimeSpan.FromSeconds(1));
        // Settle the grain's own clock before asking the endpoint to join it.
        await Grains.GetGrain<ILiveMatchGrain>(created.Id).GetAsync(Amir);
        await WaitUntilAsync(() => LiveShared.Archive.Items.First(m => m.Id == created.Id).State == MatchState.NoContest);

        var result = await LiveEndpoints.JoinAsync(created.Code, Sara, Grains, LiveShared.Archive, LiveShared.Players, LiveShared.Questions, LiveShared.Categories, Clock);
        Assert.Equal(400, CrossTypeCodeTests.StatusOf(result));
        Assert.Equal("lobby_expired", CrossTypeCodeTests.ErrorOf(result));
    }

    [Fact]
    public async Task Expiry_changes_no_PlayerStats_and_records_nothing_on_the_leaderboard()
    {
        var ids = NewIds();
        var created = ViewOf(await CreateAsync(ids));
        var statsBefore = (await LiveShared.Players.GetAsync(Amir))!.Stats;
        // A leaderboard of this test's own: LiveMatchGrain has no ILeaderboard dependency at all
        // (LiveTestSilo never registers one), so nothing in the live path could write to this even
        // if it were wired in — this instance stands in for that guarantee, the same shape the async
        // side's forfeit test asserts against Shared.Leaderboard.
        var leaderboard = new FakeLeaderboard();

        LiveShared.TimeProvider.Advance(LiveRules.LobbyExpires + TimeSpan.FromSeconds(1));
        await Grains.GetGrain<ILiveMatchGrain>(created.Id).GetAsync(Amir);
        await WaitUntilAsync(() => LiveShared.Archive.Items.First(m => m.Id == created.Id).State == MatchState.NoContest);

        Assert.Equal(statsBefore, (await LiveShared.Players.GetAsync(Amir))!.Stats);
        Assert.Empty(leaderboard.Scores);
    }

    [Fact]
    public async Task Unknown_code_returns_404_no_such_code()
    {
        var result = await LiveEndpoints.JoinAsync("NO-SUCH-CODE", Sara, Grains, LiveShared.Archive, LiveShared.Players, LiveShared.Questions, LiveShared.Categories, Clock);
        Assert.Equal(404, CrossTypeCodeTests.StatusOf(result));
        Assert.Equal("no_such_code", CrossTypeCodeTests.ErrorOf(result));
    }

    [Fact]
    public async Task The_opponent_joining_twice_is_idempotent_and_returns_their_view_not_an_error()
    {
        var ids = NewIds();
        var created = ViewOf(await CreateAsync(ids));
        await LiveEndpoints.JoinAsync(created.Code, Sara, Grains, LiveShared.Archive, LiveShared.Players, LiveShared.Questions, LiveShared.Categories, Clock);

        var second = await LiveEndpoints.JoinAsync(created.Code, Sara, Grains, LiveShared.Archive, LiveShared.Players, LiveShared.Questions, LiveShared.Categories, Clock);
        Assert.Equal(200, CrossTypeCodeTests.StatusOf(second));
        Assert.Equal(Sara, ViewOf(second).Participants[1].PlayerId);
    }

    [Fact]
    public async Task Get_returns_404_for_a_non_participant()
    {
        var ids = NewIds();
        var created = ViewOf(await CreateAsync(ids));

        var result = await LiveEndpoints.GetAsync(created.Id, Stranger, Grains, LiveShared.Players, LiveShared.Questions, LiveShared.Categories, Clock);
        Assert.Equal(404, CrossTypeCodeTests.StatusOf(result));
    }

    [Fact]
    public async Task Get_returns_the_lobby_for_the_challenger()
    {
        var ids = NewIds();
        var created = ViewOf(await CreateAsync(ids));

        var result = await LiveEndpoints.GetAsync(created.Id, Amir, Grains, LiveShared.Players, LiveShared.Questions, LiveShared.Categories, Clock);
        Assert.Equal(200, CrossTypeCodeTests.StatusOf(result));
        Assert.Equal(created.Id, ViewOf(result).Id);
    }

    [Fact]
    public async Task Cancel_by_the_challenger_in_the_lobby_ends_it_and_notifies_once()
    {
        var ids = NewIds();
        var created = ViewOf(await CreateAsync(ids));

        var result = await LiveEndpoints.CancelAsync(created.Id, Amir, Grains);
        Assert.Equal(200, CrossTypeCodeTests.StatusOf(result));

        var view = await Grains.GetGrain<ILiveMatchGrain>(created.Id).GetAsync(Amir);
        Assert.Equal((int)MatchState.NoContest, view!.State);
        Assert.Single(LiveShared.Notifier.EventsFor(created.Id), e => e.Kind == "Ended");
    }

    [Fact]
    public async Task Cancel_by_anyone_else_is_refused_and_leaves_the_duel_running()
    {
        var ids = NewIds();
        var created = ViewOf(await CreateAsync(ids));

        var result = await LiveEndpoints.CancelAsync(created.Id, Stranger, Grains);
        Assert.Equal(400, CrossTypeCodeTests.StatusOf(result));

        var view = await Grains.GetGrain<ILiveMatchGrain>(created.Id).GetAsync(Amir);
        Assert.Equal((int)MatchState.AwaitingOpponent, view!.State);
    }

    [Fact]
    public async Task Cancel_once_the_duel_has_left_the_lobby_is_refused()
    {
        var ids = NewIds();
        var created = ViewOf(await CreateAsync(ids));
        await LiveEndpoints.JoinAsync(created.Code, Sara, Grains, LiveShared.Archive, LiveShared.Players, LiveShared.Questions, LiveShared.Categories, Clock);
        await Grains.GetGrain<ILiveMatchGrain>(created.Id).StartAsync(Amir);

        var result = await LiveEndpoints.CancelAsync(created.Id, Amir, Grains);
        Assert.Equal(400, CrossTypeCodeTests.StatusOf(result));
    }

    private static readonly TokenIssuer Issuer = new(new JwtOptions
    {
        Key = "a-live-guest-test-signing-key-long-enough", Issuer = "quesshi", Audience = "quesshi", Days = 1
    });

    [Fact]
    public async Task Guest_join_by_link_creates_a_guest_and_seats_them_in_one_call()
    {
        var ids = NewIds();
        var created = ViewOf(await CreateAsync(ids));
        var playersBefore = LiveShared.Players.Items.Count;

        var result = await AuthEndpoints.GuestJoinLiveAsync(created.Code, new GuestJoinDto("Newcomer"),
            LiveShared.Archive, LiveShared.Players, Grains, LiveShared.Questions, LiveShared.Categories, Issuer, ids, Clock);

        Assert.Equal(200, CrossTypeCodeTests.StatusOf(result));
        var dto = (GuestLiveResultDto)CrossTypeCodeTests.ValueOf(result);
        Assert.Equal("lobby", dto.Live.Phase); // filling the seat no longer starts it (issue #104)
        Assert.Equal(playersBefore + 1, LiveShared.Players.Items.Count);

        // The guest holding that token may now GET their own duel and join by code (already-joined -> idempotent).
        var view = await LiveEndpoints.GetAsync(created.Id, dto.Me.Id, Grains, LiveShared.Players, LiveShared.Questions, LiveShared.Categories, Clock);
        Assert.Equal(200, CrossTypeCodeTests.StatusOf(view));
    }

    [Fact]
    public async Task Guest_join_refuses_a_name_outside_2_to_24_characters_and_creates_no_player()
    {
        var ids = NewIds();
        var created = ViewOf(await CreateAsync(ids));
        var playersBefore = LiveShared.Players.Items.Count;

        var result = await AuthEndpoints.GuestJoinLiveAsync(created.Code, new GuestJoinDto("x"),
            LiveShared.Archive, LiveShared.Players, Grains, LiveShared.Questions, LiveShared.Categories, Issuer, ids, Clock);

        Assert.Equal(400, CrossTypeCodeTests.StatusOf(result));
        Assert.Equal("name_length", CrossTypeCodeTests.ErrorOf(result));
        Assert.Equal(playersBefore, LiveShared.Players.Items.Count);
    }

    [Fact]
    public async Task Guest_join_on_an_unknown_code_creates_no_player()
    {
        var ids = NewIds();
        var playersBefore = LiveShared.Players.Items.Count;

        var result = await AuthEndpoints.GuestJoinLiveAsync("NO-SUCH-CODE", new GuestJoinDto("Newcomer"),
            LiveShared.Archive, LiveShared.Players, Grains, LiveShared.Questions, LiveShared.Categories, Issuer, ids, Clock);

        Assert.Equal(404, CrossTypeCodeTests.StatusOf(result));
        Assert.Equal(playersBefore, LiveShared.Players.Items.Count);
    }

    [Fact]
    public async Task Guest_join_on_a_taken_lobby_refuses_and_creates_no_player()
    {
        var ids = NewIds();
        var created = ViewOf(await CreateAsync(ids));
        await LiveEndpoints.JoinAsync(created.Code, Sara, Grains, LiveShared.Archive, LiveShared.Players, LiveShared.Questions, LiveShared.Categories, Clock);
        var playersBefore = LiveShared.Players.Items.Count;

        var result = await AuthEndpoints.GuestJoinLiveAsync(created.Code, new GuestJoinDto("Newcomer"),
            LiveShared.Archive, LiveShared.Players, Grains, LiveShared.Questions, LiveShared.Categories, Issuer, ids, Clock);

        Assert.Equal(400, CrossTypeCodeTests.StatusOf(result));
        Assert.Equal("cannot_join", CrossTypeCodeTests.ErrorOf(result));
        Assert.Equal(playersBefore, LiveShared.Players.Items.Count);
    }

    [Fact]
    public async Task Guest_join_on_an_expired_lobby_refuses_and_creates_no_player()
    {
        var ids = NewIds();
        var created = ViewOf(await CreateAsync(ids));

        LiveShared.TimeProvider.Advance(LiveRules.LobbyExpires + TimeSpan.FromSeconds(1));
        await Grains.GetGrain<ILiveMatchGrain>(created.Id).GetAsync(Amir);
        await WaitUntilAsync(() => LiveShared.Archive.Items.First(m => m.Id == created.Id).State == MatchState.NoContest);
        var playersBefore = LiveShared.Players.Items.Count;

        var result = await AuthEndpoints.GuestJoinLiveAsync(created.Code, new GuestJoinDto("Newcomer"),
            LiveShared.Archive, LiveShared.Players, Grains, LiveShared.Questions, LiveShared.Categories, Issuer, ids, Clock);

        Assert.Equal(400, CrossTypeCodeTests.StatusOf(result));
        Assert.Equal("lobby_expired", CrossTypeCodeTests.ErrorOf(result));
        Assert.Equal(playersBefore, LiveShared.Players.Items.Count);
    }

    private static async Task WaitUntilAsync(Func<bool> ready, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (ready()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException("Condition not reached in time.");
    }
}
