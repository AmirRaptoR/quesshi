using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Infrastructure;
using Quesshi.Server.Live;

namespace Quesshi.Server.Tests;

/// <summary>Covers <see cref="LobbyHub.Challenge"/>'s own checks — not-a-friend, self — which run
/// before the grain is ever touched, plus the paths that need a real grain: opening the lobby the
/// challenge points at, and reaching an offline friend (issue #51: an invitation is no longer refused
/// just because the target is not connected right now — it is stored and delivered on their next
/// connect, so a friend does not have to be online at all to be challenged).</summary>
[Collection(nameof(LiveClusterCollection))]
public class LobbyHubChallengeTests(LiveClusterFixture fixture)
{
    private sealed class FakeHubCallerContext(ClaimsPrincipal user) : HubCallerContext
    {
        public override string ConnectionId { get; } = Guid.NewGuid().ToString("N");
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User { get; } = user;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }

    private static ClaimsPrincipal PlayerUser(string id) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "test"));

    private sealed class FixedPresence(bool online) : IPresence
    {
        public Task MarkOnlineAsync(string playerId, TimeSpan ttl, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkOfflineAsync(string playerId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyCollection<string>> OnlineAsync(IReadOnlyCollection<string> playerIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyCollection<string>>(online ? [.. playerIds] : []);
    }

    [Fact]
    public async Task Challenging_an_offline_friend_still_reaches_the_grain_and_is_delivered_on_their_next_connect()
    {
        var players = new FakePlayers();
        players.Items.Add(Player.Register("me-1", "me@example.com", "Me", Language.En, DateTimeOffset.UtcNow));
        players.Items.Add(Player.Register("friend-1", "f@example.com", "Friend", Language.En, DateTimeOffset.UtcNow));
        players.Items[0].AddFriend("friend-1");

        var hub = new LobbyHub(fixture.Cluster.GrainFactory, new FixedPresence(online: false), new FakeLobbyNotifier(), players, new IdFactory(), new FakeArchive());
        hub.Context = new FakeHubCallerContext(PlayerUser("me-1"));

        var result = await hub.Challenge("friend-1", "en", 10, [], []);

        Assert.Equal((int)LiveChallengeResult.Sent, result);
        var pending = await fixture.Cluster.GrainFactory.GetGrain<Grains.Abstractions.ILiveMatchmakingGrain>(0).PendingForAsync("friend-1");
        Assert.Contains(pending, c => c.ChallengerId == "me-1");
    }

    [Fact]
    public async Task Challenging_somebody_who_is_not_a_friend_is_refused()
    {
        var players = new FakePlayers();
        players.Items.Add(Player.Register("me-2", "me2@example.com", "Me", Language.En, DateTimeOffset.UtcNow));
        players.Items.Add(Player.Register("stranger-2", "s@example.com", "Stranger", Language.En, DateTimeOffset.UtcNow));

        var hub = new LobbyHub(null!, new FixedPresence(online: true), new FakeLobbyNotifier(), players, new IdFactory(), new FakeArchive());
        hub.Context = new FakeHubCallerContext(PlayerUser("me-2"));

        var result = await hub.Challenge("stranger-2", "en", 10, [], []);

        Assert.Equal((int)LiveChallengeResult.NotFound, result);
    }

    /// <summary>
    /// The narrow relaxation issue #52 adds: a former co-participant reaches the grain exactly like a
    /// friend does, even though neither ever added the other. Scoped to people who demonstrably just
    /// played together — proven here by an archived row naming both of them as participants.
    /// </summary>
    [Fact]
    public async Task Challenging_a_former_co_participant_who_is_not_a_friend_still_reaches_the_grain()
    {
        var players = new FakePlayers();
        players.Items.Add(Player.Register("me-5", "me5@example.com", "Me", Language.En, DateTimeOffset.UtcNow));
        players.Items.Add(Player.Register("rando-5", "r5@example.com", "Rando", Language.En, DateTimeOffset.UtcNow));
        // Neither adds the other as a friend — the relaxation is what has to carry this, not Friends.

        var archive = new FakeArchive();
        archive.Items.Add(new ArchivedMatch("past-duel-5", "PAST05", Language.En, "me-5", "rando-5", "me-5", false,
            FakeArchive.TestResults("me-5", "rando-5", 100, 40), MatchState.Resolved, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], IsLive: true));

        var hub = new LobbyHub(fixture.Cluster.GrainFactory, new FixedPresence(online: true), new FakeLobbyNotifier(), players, new IdFactory(), archive);
        hub.Context = new FakeHubCallerContext(PlayerUser("me-5"));

        var result = await hub.Challenge("rando-5", "en", 10, [], []);

        Assert.Equal((int)LiveChallengeResult.Sent, result);
        var pending = await fixture.Cluster.GrainFactory.GetGrain<Grains.Abstractions.ILiveMatchmakingGrain>(0).PendingForAsync("rando-5");
        Assert.Contains(pending, c => c.ChallengerId == "me-5");
    }

    /// <summary>The relaxation is scoped to a real shared match, not to having played at all: an
    /// archive that holds duels for *other* people changes nothing for two strangers.</summary>
    [Fact]
    public async Task Challenging_someone_with_no_shared_match_is_still_refused_even_with_an_unrelated_archive()
    {
        var players = new FakePlayers();
        players.Items.Add(Player.Register("me-6", "me6@example.com", "Me", Language.En, DateTimeOffset.UtcNow));
        players.Items.Add(Player.Register("stranger-6", "s6@example.com", "Stranger", Language.En, DateTimeOffset.UtcNow));

        var archive = new FakeArchive();
        archive.Items.Add(new ArchivedMatch("unrelated", "UNREL01", Language.En, "someone-else", "someone-else-2", null, false,
            FakeArchive.TestResults("someone-else", "someone-else-2", 0, 0), MatchState.Resolved, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], IsLive: true));

        var hub = new LobbyHub(null!, new FixedPresence(online: true), new FakeLobbyNotifier(), players, new IdFactory(), archive);
        hub.Context = new FakeHubCallerContext(PlayerUser("me-6"));

        var result = await hub.Challenge("stranger-6", "en", 10, [], []);

        Assert.Equal((int)LiveChallengeResult.NotFound, result);
    }

    [Fact]
    public async Task Challenging_yourself_through_the_hub_is_refused()
    {
        var hub = new LobbyHub(null!, new FixedPresence(online: true), new FakeLobbyNotifier(), new FakePlayers(), new IdFactory(), new FakeArchive());
        hub.Context = new FakeHubCallerContext(PlayerUser("solo-3"));

        var result = await hub.Challenge("solo-3", "en", 10, [], []);

        Assert.Equal((int)LiveChallengeResult.SelfChallenge, result);
    }

    [Fact]
    public async Task Challenging_an_online_friend_reaches_the_grain_and_is_delivered()
    {
        var players = new FakePlayers();
        players.Items.Add(Player.Register("me-4", "me4@example.com", "Me", Language.En, DateTimeOffset.UtcNow));
        players.Items.Add(Player.Register("friend-4", "f4@example.com", "Friend", Language.En, DateTimeOffset.UtcNow));
        players.Items[0].AddFriend("friend-4");

        var notifier = new FakeLobbyNotifier();
        var hub = new LobbyHub(fixture.Cluster.GrainFactory, new FixedPresence(online: true), notifier, players, new IdFactory(), new FakeArchive());
        hub.Context = new FakeHubCallerContext(PlayerUser("me-4"));

        var result = await hub.Challenge("friend-4", "en", 10, [], []);

        Assert.Equal((int)LiveChallengeResult.Sent, result);
        var pending = await fixture.Cluster.GrainFactory.GetGrain<Grains.Abstractions.ILiveMatchmakingGrain>(0).PendingForAsync("friend-4");
        Assert.Contains(pending, c => c.ChallengerId == "me-4");
    }
}
