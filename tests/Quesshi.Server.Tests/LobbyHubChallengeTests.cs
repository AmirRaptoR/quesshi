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

        var hub = new LobbyHub(fixture.Cluster.GrainFactory, new FixedPresence(online: false), new FakeLobbyNotifier(), players, new IdFactory());
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

        var hub = new LobbyHub(null!, new FixedPresence(online: true), new FakeLobbyNotifier(), players, new IdFactory());
        hub.Context = new FakeHubCallerContext(PlayerUser("me-2"));

        var result = await hub.Challenge("stranger-2", "en", 10, [], []);

        Assert.Equal((int)LiveChallengeResult.NotFound, result);
    }

    [Fact]
    public async Task Challenging_yourself_through_the_hub_is_refused()
    {
        var hub = new LobbyHub(null!, new FixedPresence(online: true), new FakeLobbyNotifier(), new FakePlayers(), new IdFactory());
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
        var hub = new LobbyHub(fixture.Cluster.GrainFactory, new FixedPresence(online: true), notifier, players, new IdFactory());
        hub.Context = new FakeHubCallerContext(PlayerUser("me-4"));

        var result = await hub.Challenge("friend-4", "en", 10, [], []);

        Assert.Equal((int)LiveChallengeResult.Sent, result);
        var pending = await fixture.Cluster.GrainFactory.GetGrain<Grains.Abstractions.ILiveMatchmakingGrain>(0).PendingForAsync("friend-4");
        Assert.Contains(pending, c => c.ChallengerId == "me-4");
    }
}
