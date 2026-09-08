using Quesshi.Domain;
using Quesshi.Grains.Abstractions;
using Quesshi.Infrastructure;
using Quesshi.Server.Live;

namespace Quesshi.Server.Tests;

/// <summary>
/// Issue #53's last gap: inviting a friend into a lobby that already exists, rather than
/// <see cref="LobbyHub.Challenge"/>'s own always-fresh capacity-2 duel. Covers
/// <see cref="LobbyHub.InviteToLobby"/> pointing at a real lobby instead of minting one, the guest
/// refusal that keeps a friend's owner from ever seeing a dead invitation, that <see cref="LobbyHub.Challenge"/>
/// itself is untouched by the refactor the two methods now share, and the accept-time distinction
/// between a lobby that filled up and one that never had a seat to begin with.
/// </summary>
[Collection(nameof(LiveClusterCollection))]
public class LobbyHubInviteToLobbyTests(LiveClusterFixture fixture)
{
    private static int _n;

    private static Player RealPlayer(string id) => Player.Register(id, $"{id}@example.com", id, Language.En, DateTimeOffset.UtcNow);
    private static Player GuestPlayer(string id) => Player.Guest(id, id, Language.En, DateTimeOffset.UtcNow);

    private static LobbyHub NewHub(IGrainFactory grains, FakePlayers players, string playerId, bool isGuest = false)
        => new(grains, new FakePresence(), new FakeLobbyNotifier(), players, new IdFactory(), new FakeArchive())
        {
            Context = new FakeHubCallerContext(playerId, isGuest)
        };

    /// <summary>Enough approved English questions for a capacity-2 lobby's own auto-start draw once
    /// its last seat fills — only <see cref="Accepting_a_challenge_whose_lobby_has_since_filled_is_refused_as_LobbyFull"/>
    /// ever needs a real draw to happen; every other test here never gets past the lobby phase.</summary>
    private static Category SeedCategory()
    {
        var id = $"invite-cat-{Interlocked.Increment(ref _n)}";
        var category = new Category(id, "دسته", "Category", "globe", "#336699");
        LiveShared.Categories.Items.Add(category);

        for (var i = 0; i < 10; i++)
        {
            var qid = $"{id}-q{i}";
            LiveShared.Questions.Items.Add(Question.Create(qid, Language.En, id, MatchRules.LevelForSlot(i, 10),
                $"q {i}", ["right", "w1", "w2", "w3"], 0, LiveShared.TimeProvider.GetUtcNow(), status: QuestionStatus.Approved));
        }

        return category;
    }

    [Fact]
    public async Task InviteToLobby_targets_the_lobby_it_is_given_and_accepting_joins_that_same_lobby()
    {
        var n = Interlocked.Increment(ref _n);
        var ownerId = $"inv-owner-{n}";
        var friendId = $"inv-friend-{n}";
        var lobbyId = Guid.NewGuid().ToString("N");

        var players = new FakePlayers();
        players.Items.Add(RealPlayer(ownerId));
        var friend = RealPlayer(friendId);
        players.Items.Add(friend);
        players.Items[0].AddFriend(friendId);

        var grains = fixture.Cluster.GrainFactory;
        var lobby = grains.GetGrain<ILiveMatchGrain>(lobbyId);
        await lobby.CreateLobbyAsync($"INV{n}", ownerId, (int)Language.En, 10, [], [], capacity: 3);

        var ownerHub = NewHub(grains, players, ownerId);
        var result = await ownerHub.InviteToLobby(friendId, lobbyId);
        Assert.Equal((int)LiveChallengeResult.Sent, result);

        var matchmaking = grains.GetGrain<ILiveMatchmakingGrain>(0);
        var pending = (await matchmaking.PendingForAsync(friendId)).Single();
        Assert.Equal(lobbyId, pending.LobbyId); // the invitation points at the lobby already given it, not a fresh one
        Assert.Equal(ownerId, pending.ChallengerId);

        var friendHub = NewHub(grains, players, friendId);
        var accepted = await friendHub.Accept(pending.ChallengeId);

        Assert.Equal((int)LiveChallengeResult.Accepted, accepted.Result);
        Assert.Equal(lobbyId, accepted.MatchId); // joined the very lobby InviteToLobby was given, no new one minted

        var view = await lobby.GetAsync(friendId);
        Assert.NotNull(view);
        Assert.Contains(friendId, view!.Participants);
    }

    [Fact]
    public async Task Inviting_someone_who_is_not_a_friend_into_a_lobby_is_refused_without_reaching_the_grain()
    {
        var n = Interlocked.Increment(ref _n);
        var ownerId = $"inv-owner-{n}";
        var strangerId = $"inv-stranger-{n}";
        var lobbyId = Guid.NewGuid().ToString("N");

        var players = new FakePlayers();
        players.Items.Add(RealPlayer(ownerId));
        players.Items.Add(RealPlayer(strangerId));

        var grains = fixture.Cluster.GrainFactory;
        await grains.GetGrain<ILiveMatchGrain>(lobbyId).CreateLobbyAsync($"INV{n}", ownerId, (int)Language.En, 10, [], [], capacity: 3);

        var hub = NewHub(grains, players, ownerId);
        var result = await hub.InviteToLobby(strangerId, lobbyId);

        Assert.Equal((int)LiveChallengeResult.NotFound, result);
        Assert.Empty(await grains.GetGrain<ILiveMatchmakingGrain>(0).PendingForAsync(strangerId));
    }

    /// <summary>
    /// The guest limit issue #53 asks not to be weakened: <c>OnConnectedAsync</c> aborts every guest
    /// connection to this hub outright, so an invitation minted for one would sit undelivered forever.
    /// Refusing here means <c>Lobby.razor</c>'s own friend list never even offers the button — this is
    /// the belt to that braces, proving the hub itself never mints the dead invitation either way.
    /// </summary>
    [Fact]
    public async Task A_guest_friend_is_never_sent_an_in_app_invitation_into_an_existing_lobby()
    {
        var n = Interlocked.Increment(ref _n);
        var ownerId = $"inv-owner-{n}";
        var guestId = $"inv-guest-{n}";
        var lobbyId = Guid.NewGuid().ToString("N");

        var players = new FakePlayers();
        players.Items.Add(RealPlayer(ownerId));
        players.Items.Add(GuestPlayer(guestId));
        players.Items[0].AddFriend(guestId);

        var grains = fixture.Cluster.GrainFactory;
        await grains.GetGrain<ILiveMatchGrain>(lobbyId).CreateLobbyAsync($"INV{n}", ownerId, (int)Language.En, 10, [], [], capacity: 3);

        var hub = NewHub(grains, players, ownerId);
        var result = await hub.InviteToLobby(guestId, lobbyId);

        Assert.NotEqual((int)LiveChallengeResult.Sent, result);
        Assert.Empty(await grains.GetGrain<ILiveMatchmakingGrain>(0).PendingForAsync(guestId));
    }

    /// <summary>
    /// The regression guard for the Profile page's own button: <see cref="LobbyHub.Challenge"/> must
    /// still behave exactly as it always has after being refactored to share its friendship gate with
    /// <see cref="LobbyHub.InviteToLobby"/> — a fresh capacity-2 lobby per call, never a shared one.
    /// </summary>
    [Fact]
    public async Task Challenge_still_mints_its_own_fresh_capacity_two_lobby_per_call()
    {
        var n = Interlocked.Increment(ref _n);
        var challengerId = $"inv-challenger-{n}";
        var friend1Id = $"inv-f1-{n}";
        var friend2Id = $"inv-f2-{n}";

        var players = new FakePlayers();
        players.Items.Add(RealPlayer(challengerId));
        players.Items.Add(RealPlayer(friend1Id));
        players.Items.Add(RealPlayer(friend2Id));
        players.Items[0].AddFriend(friend1Id);
        players.Items[0].AddFriend(friend2Id);

        var grains = fixture.Cluster.GrainFactory;
        var hub = NewHub(grains, players, challengerId);

        var result1 = await hub.Challenge(friend1Id, "en", 10, [], []);
        var result2 = await hub.Challenge(friend2Id, "en", 10, [], []);
        Assert.Equal((int)LiveChallengeResult.Sent, result1);
        Assert.Equal((int)LiveChallengeResult.Sent, result2);

        var matchmaking = grains.GetGrain<ILiveMatchmakingGrain>(0);
        var pending1 = (await matchmaking.PendingForAsync(friend1Id)).Single();
        var pending2 = (await matchmaking.PendingForAsync(friend2Id)).Single();

        Assert.NotEqual(pending1.LobbyId, pending2.LobbyId); // two calls, two fresh lobbies — never shared

        var view1 = await grains.GetGrain<ILiveMatchGrain>(pending1.LobbyId).GetAsync(challengerId);
        var view2 = await grains.GetGrain<ILiveMatchGrain>(pending2.LobbyId).GetAsync(challengerId);
        Assert.Equal(2, view1!.Capacity);
        Assert.Equal(2, view2!.Capacity);
    }

    /// <summary>
    /// <c>LiveJoinResult</c> already tells a full lobby apart from a started-but-unfilled one and from
    /// an expired one; this proves that distinction survives all the way out to what an accepting
    /// invitee sees, rather than being folded back into one generic failure the moment the lobby
    /// filled up from under them.
    /// </summary>
    [Fact]
    public async Task Accepting_a_challenge_whose_lobby_has_since_filled_is_refused_as_LobbyFull()
    {
        var n = Interlocked.Increment(ref _n);
        var ownerId = $"inv-owner-{n}";
        var friendId = $"inv-friend-{n}";
        var fillerId = $"inv-filler-{n}";
        var lobbyId = Guid.NewGuid().ToString("N");
        var categoryId = SeedCategory().Id;

        var players = new FakePlayers();
        players.Items.Add(RealPlayer(ownerId));
        players.Items.Add(RealPlayer(friendId));
        players.Items[0].AddFriend(friendId);

        var grains = fixture.Cluster.GrainFactory;
        var lobby = grains.GetGrain<ILiveMatchGrain>(lobbyId);
        await lobby.CreateLobbyAsync($"INV{n}", ownerId, (int)Language.En, 10, [categoryId], [], capacity: 2);

        var ownerHub = NewHub(grains, players, ownerId);
        var sent = await ownerHub.InviteToLobby(friendId, lobbyId);
        Assert.Equal((int)LiveChallengeResult.Sent, sent);
        var pending = (await grains.GetGrain<ILiveMatchmakingGrain>(0).PendingForAsync(friendId)).Single();

        // A capacity-2 lobby's last seat filling also starts it, synchronously — by the time the
        // invited friend gets around to accepting, there is no seat left at all.
        var fillerJoin = (LiveJoinResult)await lobby.JoinAsync(fillerId);
        Assert.Equal(LiveJoinResult.Joined, fillerJoin);

        var friendHub = NewHub(grains, players, friendId);
        var accepted = await friendHub.Accept(pending.ChallengeId);

        Assert.Equal((int)LiveChallengeResult.LobbyFull, accepted.Result);
        Assert.Null(accepted.MatchId);
        Assert.DoesNotContain(friendId, (await lobby.GetAsync(ownerId))!.Participants);
    }
}
