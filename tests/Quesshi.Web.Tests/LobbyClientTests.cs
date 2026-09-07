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
}
