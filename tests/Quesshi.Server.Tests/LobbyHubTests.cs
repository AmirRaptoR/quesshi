using Microsoft.AspNetCore.SignalR.Client;
using Quesshi.Domain;
using Quesshi.Server.Live;

namespace Quesshi.Server.Tests;

/// <summary>
/// The lobby hub's whole job: mark a real player online while connected, offline once the connection
/// is gone, keep them online across a heartbeat, and refuse a guest outright. No Mongo, no Redis — a
/// <see cref="FakePresence"/> stands in for the port; the grain factory is only there because the hub
/// also checks for a pending challenge on connect, which <see cref="LobbyHubChallengeTests"/> covers.
/// </summary>
[Collection(nameof(LiveClusterCollection))]
public class LobbyHubTests(LiveClusterFixture fixture)
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static Player RealPlayer(string id) => Player.Register(id, $"{id}@example.com", id, Language.En, T0);
    private static Player Guest(string id) => Player.Guest(id, id, Language.En, T0);

    [Fact]
    public async Task Connecting_marks_the_player_online()
    {
        await using var host = new LobbyHubTestHost(fixture.Cluster.GrainFactory);
        var token = host.TokenIssuer.Issue(RealPlayer("p1"));
        await using var connection = host.NewConnection(token);

        await connection.StartAsync();

        Assert.True(host.Presence.IsOnline("p1"));
    }

    [Fact]
    public async Task Disconnecting_marks_the_player_offline()
    {
        await using var host = new LobbyHubTestHost(fixture.Cluster.GrainFactory);
        var token = host.TokenIssuer.Issue(RealPlayer("p1"));
        var connection = host.NewConnection(token);
        await connection.StartAsync();
        Assert.True(host.Presence.IsOnline("p1"));

        await connection.DisposeAsync();
        await Task.Delay(200); // the server-side OnDisconnectedAsync runs asynchronously after the client tears down

        Assert.False(host.Presence.IsOnline("p1"));
    }

    [Fact]
    public async Task A_guests_connection_is_aborted_and_no_presence_key_is_written()
    {
        await using var host = new LobbyHubTestHost(fixture.Cluster.GrainFactory);
        var token = host.TokenIssuer.Issue(Guest("g1"));
        await using var connection = host.NewConnection(token);
        var closed = new TaskCompletionSource();
        connection.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };

        // The handshake still completes; the server aborts the connection right after OnConnectedAsync runs.
        await connection.StartAsync();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(host.Presence.IsOnline("g1"));
    }

    [Fact]
    public async Task Heartbeat_refreshes_the_TTL_of_an_already_online_player_and_leaves_them_online()
    {
        await using var host = new LobbyHubTestHost(fixture.Cluster.GrainFactory);
        var token = host.TokenIssuer.Issue(RealPlayer("p1"));
        await using var connection = host.NewConnection(token);
        await connection.StartAsync();

        await connection.InvokeAsync("Heartbeat");

        Assert.True(host.Presence.IsOnline("p1"));
    }

    [Fact]
    public void The_presence_TTL_is_a_named_constant_of_sixty_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), LobbyHub.PresenceTtl);
    }
}
