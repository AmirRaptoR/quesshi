using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Orleans;
using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Server.Auth;
using Quesshi.Server.Live;

namespace Quesshi.Server.Tests;

/// <summary>
/// The hub method refuses a guest caller explicitly — provable by calling it directly against a fake
/// <see cref="HubCallerContext"/>, with no connection ever standing up. The grain factory here throws
/// if touched at all, so a guest reaching the grain would fail the test loudly rather than quietly.
/// </summary>
public class LobbyHubGuestGateTests
{
    private sealed class FakeHubCallerContext(ClaimsPrincipal? user) : HubCallerContext
    {
        public bool Aborted { get; private set; }
        public override string ConnectionId { get; } = Guid.NewGuid().ToString("N");
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User { get; } = user;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() => Aborted = true;
    }

    private sealed class TrackingPresence : IPresence
    {
        public bool WasMarkedOnline { get; private set; }

        public Task MarkOnlineAsync(string playerId, TimeSpan ttl, CancellationToken ct = default)
        {
            WasMarkedOnline = true;
            return Task.CompletedTask;
        }

        public Task MarkOfflineAsync(string playerId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyCollection<string>> OnlineAsync(IReadOnlyCollection<string> playerIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyCollection<string>>([]);
    }

    private static LobbyHub NewHub(ClaimsPrincipal? user)
    {
        // A guest is refused before the hub ever reaches the grain factory, so a null one here still
        // proves the point: touching it at all would throw a NullReferenceException and fail the test.
        var hub = new LobbyHub(null!, new NeverOnlinePresence(), new FakeLobbyNotifier(), new FakePlayers(), new ThrowingIdFactory());
        hub.Context = new FakeHubCallerContext(user);
        return hub;
    }

    private static ClaimsPrincipal GuestUser() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "guest-1"), new Claim(TokenIssuer.GuestClaim, "1")], "test"));

    private sealed class NeverOnlinePresence : IPresence
    {
        public Task MarkOnlineAsync(string playerId, TimeSpan ttl, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkOfflineAsync(string playerId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyCollection<string>> OnlineAsync(IReadOnlyCollection<string> playerIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyCollection<string>>([.. playerIds]);
    }

    private sealed class ThrowingIdFactory : IIdFactory
    {
        public string NewId() => throw new InvalidOperationException("A guest must never mint a challenge id.");
        public string NewMatchCode() => throw new InvalidOperationException();
    }

    [Fact]
    public async Task A_guest_calling_Challenge_is_refused_and_no_challenge_is_created()
    {
        var hub = NewHub(GuestUser());

        var result = await hub.Challenge("someone", "en", 10, [], []);

        Assert.NotEqual((int)LiveChallengeResult.Sent, result);
    }

    [Fact]
    public async Task A_guest_calling_Accept_is_refused()
    {
        var hub = NewHub(GuestUser());

        var result = await hub.Accept("some-challenge");

        Assert.NotEqual((int)LiveChallengeResult.Accepted, result.Result);
        Assert.Null(result.MatchId);
    }

    [Fact]
    public async Task A_guest_calling_Decline_is_refused()
    {
        var hub = NewHub(GuestUser());

        var result = await hub.Decline("some-challenge");

        Assert.NotEqual((int)LiveChallengeResult.Declined, result);
    }

    /// <summary>Structural refusal: the connection itself is aborted, before presence or delivery ever run.</summary>
    [Fact]
    public async Task A_guest_connection_is_aborted_and_never_marked_present_or_delivered_anything()
    {
        var presence = new TrackingPresence();
        var notifier = new FakeLobbyNotifier();
        var hub = new LobbyHub(null!, presence, notifier, new FakePlayers(), new ThrowingIdFactory());
        var context = new FakeHubCallerContext(GuestUser());
        hub.Context = context;

        await hub.OnConnectedAsync();

        Assert.True(context.Aborted);
        Assert.False(presence.WasMarkedOnline);
        Assert.Empty(notifier.EventsFor("guest-1"));
    }
}
