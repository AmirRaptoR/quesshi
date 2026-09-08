using Microsoft.AspNetCore.SignalR.Client;
using Quesshi.Shared;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

/// <summary>
/// LiveClient's reconnect wiring, tested against a real but never-started HubConnection — so this
/// runs with no socket, no server and no containers, per issue #11's client-side split.
/// </summary>
public class LiveClientTests
{
    private static LiveViewDto SampleView(string matchId, DateTimeOffset serverNow) => new(
        matchId, [new("challenger", "Challenger", "c-seed", false), new("opponent", "Opponent", "o-seed", false)],
        "active", "question", serverNow.AddSeconds(10), serverNow,
        0, 5, [], [], [], null, false, [], serverNow, null);

    private static LiveClient NewClient()
        => new(new HubConnectionBuilder().WithUrl("http://localhost/hub/live").WithAutomaticReconnect().Build());

    [Fact]
    public void Skew_is_the_difference_between_server_time_and_local_time()
    {
        var serverNow = new DateTimeOffset(2026, 9, 7, 12, 0, 5, TimeSpan.Zero);
        var localNow = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromSeconds(5), LiveClient.ComputeSkew(serverNow, localNow));
    }

    [Fact]
    public async Task Joining_captures_skew_from_the_first_LiveViewDto_of_the_connection()
    {
        await using var client = NewClient();
        var serverNow = DateTimeOffset.UtcNow.AddMinutes(3); // an obviously-skewed clock
        client.JoinInvokerOverrideForTests = id => Task.FromResult(SampleView(id, serverNow));

        await client.JoinAsync("m1");

        Assert.True(client.Skew > TimeSpan.FromMinutes(2.5));
    }

    /// <summary>
    /// Issue #53's lobby page: the async-lobby twin of JoinAsync, against a never-started connection —
    /// the same "without a live socket" case QueueRandomAsync/ChallengeAsync/etc. all prove for
    /// LobbyClient. Unlike JoinAsync (which has no such guard and lets a real invoke failure surface,
    /// since JoinInvokerOverrideForTests exists specifically to avoid ever hitting the wire in a test),
    /// this returns false rather than throwing — there is no override seam for it, so a real duel
    /// grain would have to answer, and a page calling this while disconnected must not crash on it.
    /// </summary>
    [Fact]
    public async Task JoinAsyncLobbyAsync_does_not_throw_when_the_connection_is_not_active()
    {
        await using var client = NewClient();

        var joined = await client.JoinAsyncLobbyAsync("m1");

        Assert.False(joined);
    }

    /// <summary>
    /// Never started, so this is exactly "without a live socket". Invokes the connection's own
    /// Reconnected delegate directly (rather than calling LiveClient's handler by name) so this
    /// actually proves the handler was subscribed to HubConnection.Reconnected, not merely that the
    /// method exists on LiveClient.
    /// </summary>
    [Fact]
    public async Task Reconnecting_re_issues_Join_for_the_match_that_was_joined()
    {
        await using var client = NewClient();
        var joinedMatchIds = new List<string>();
        client.JoinInvokerOverrideForTests = id =>
        {
            joinedMatchIds.Add(id);
            return Task.FromResult(SampleView(id, DateTimeOffset.UtcNow));
        };

        await client.JoinAsync("m1");

        var reconnectedField = typeof(HubConnection).GetField("Reconnected",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var reconnectedHandler = (Func<string?, Task>)reconnectedField.GetValue(client.Connection)!;
        await reconnectedHandler("new-connection-id");

        Assert.Equal(["m1", "m1"], joinedMatchIds);
        Assert.Equal(HubConnectionState.Disconnected, client.Connection.State);
    }

    /// <summary>Before any Join, a reconnect has nothing to re-fetch — it must not guess a match id.</summary>
    [Fact]
    public async Task Reconnecting_before_any_Join_does_nothing()
    {
        await using var client = NewClient();
        var joinCalls = 0;
        client.JoinInvokerOverrideForTests = _ => { joinCalls++; return Task.FromResult(SampleView("x", DateTimeOffset.UtcNow)); };

        await client.OnReconnectedAsync(null);

        Assert.Equal(0, joinCalls);
    }

    /// <summary>
    /// A page has no other way to learn what happened while the socket was down — a missed reveal, a
    /// missed round start, even a missed ending — than the rejoin's own catch-up view. Without this
    /// event a reconnecting client is stuck showing whatever phase it was in when it dropped.
    /// </summary>
    [Fact]
    public async Task Reconnecting_raises_Rejoined_with_the_fresh_catchup_view()
    {
        await using var client = NewClient();
        client.JoinInvokerOverrideForTests = id => Task.FromResult(SampleView(id, DateTimeOffset.UtcNow));
        await client.JoinAsync("m1");

        LiveViewDto? rejoined = null;
        client.Rejoined += v => rejoined = v;

        await client.OnReconnectedAsync("new-connection-id");

        Assert.NotNull(rejoined);
        Assert.Equal("m1", rejoined!.Id);
    }

    /// <summary>Before any Join, a reconnect fetches nothing, so it must not raise Rejoined either.</summary>
    [Fact]
    public async Task Reconnecting_before_any_Join_does_not_raise_Rejoined()
    {
        await using var client = NewClient();
        client.JoinInvokerOverrideForTests = id => Task.FromResult(SampleView(id, DateTimeOffset.UtcNow));

        var raised = false;
        client.Rejoined += _ => raised = true;

        await client.OnReconnectedAsync(null);

        Assert.False(raised);
    }

    [Fact]
    public void The_connection_is_configured_for_automatic_reconnect()
    {
        // WithAutomaticReconnect installs a retry policy; without it, HubConnectionBuilder leaves
        // reconnection off entirely and a dropped socket would just stay dropped.
        var withReconnect = new HubConnectionBuilder().WithUrl("http://localhost/hub/live").WithAutomaticReconnect().Build();
        var field = typeof(HubConnection).GetField("_reconnectPolicy", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        Assert.NotNull(field!.GetValue(withReconnect));
    }
}
