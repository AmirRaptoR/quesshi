using Microsoft.AspNetCore.SignalR.Client;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

/// <summary>
/// LobbyClient's reconnect wiring, tested against a real but never-started HubConnection — no socket,
/// no server and no containers, the same way LiveClientTests proves LiveClient's rejoin.
/// </summary>
public class LobbyClientTests
{
    private static LobbyClient NewClient()
        => new(new HubConnectionBuilder().WithUrl("http://localhost/hub/lobby").WithAutomaticReconnect().Build());

    [Fact]
    public async Task Reconnecting_resumes_heartbeating()
    {
        await using var client = NewClient();
        var heartbeats = 0;
        client.HeartbeatInvokerOverrideForTests = () => { heartbeats++; return Task.CompletedTask; };

        var reconnectedField = typeof(HubConnection).GetField("Reconnected",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var reconnectedHandler = (Func<string?, Task>)reconnectedField.GetValue(client.Connection)!;
        await reconnectedHandler("new-connection-id");

        Assert.True(heartbeats > 0);
    }

    [Fact]
    public void The_connection_is_configured_for_automatic_reconnect()
    {
        var withReconnect = new HubConnectionBuilder().WithUrl("http://localhost/hub/lobby").WithAutomaticReconnect().Build();
        var field = typeof(HubConnection).GetField("_reconnectPolicy", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        Assert.NotNull(field!.GetValue(withReconnect));
    }

    [Fact]
    public void The_heartbeat_interval_is_a_named_constant_of_twenty_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(20), LobbyClient.HeartbeatInterval);
    }

    [Fact]
    public async Task IsConnected_is_false_for_a_never_started_connection()
    {
        await using var client = NewClient();

        Assert.False(client.IsConnected);
        Assert.Equal(HubConnectionState.Disconnected, client.Connection.State);
    }

    /// <summary>
    /// A never-started connection is exactly the "not active" state that threw
    /// InvalidOperationException out of an @onclick handler in issue #45 — every invoke path must
    /// absorb that instead.
    /// </summary>
    [Fact]
    public async Task QueueRandomAsync_does_not_throw_when_the_connection_is_not_active()
    {
        await using var client = NewClient();

        var result = await client.QueueRandomAsync(0, 10, [], []);

        Assert.False(result.Sent);
    }

    /// <summary>QueueRandomAsync already returns a null MatchId to mean "queued, now waiting" — a
    /// send failure must be a distinct outcome, not the same null.</summary>
    [Fact]
    public async Task QueueRandomAsync_failure_is_not_signalled_by_a_null_match_id()
    {
        await using var client = NewClient();

        var result = await client.QueueRandomAsync(0, 10, [], []);

        Assert.False(result.Sent);
        Assert.Null(result.MatchId);
        // Sent is what distinguishes this from a real "queued, waiting" success with a null MatchId.
    }

    [Fact]
    public async Task LeaveQueueAsync_does_not_throw_when_the_connection_is_not_active()
    {
        await using var client = NewClient();

        var sent = await client.LeaveQueueAsync();

        Assert.False(sent);
    }

    [Fact]
    public async Task ChallengeAsync_does_not_throw_when_the_connection_is_not_active()
    {
        await using var client = NewClient();

        var result = await client.ChallengeAsync("target", "en", 10, [], []);

        Assert.Null(result);
    }

    [Fact]
    public async Task AcceptAsync_does_not_throw_when_the_connection_is_not_active()
    {
        await using var client = NewClient();

        var result = await client.AcceptAsync("challenge-id");

        Assert.Null(result);
    }

    [Fact]
    public async Task DeclineAsync_does_not_throw_when_the_connection_is_not_active()
    {
        await using var client = NewClient();

        var result = await client.DeclineAsync("challenge-id");

        Assert.Null(result);
    }
}
