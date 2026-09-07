using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Quesshi.Domain;
using Quesshi.Grains;
using Quesshi.Grains.Abstractions;
using Quesshi.Server.Live;

namespace Quesshi.Server.Tests;

/// <summary>
/// The lobby hub's whole job: mark a real player online while connected, offline once the connection
/// is gone, keep them online across a heartbeat, refuse a guest outright, and — for the random live
/// queue — ride QueueRandom/LeaveQueue over that same connection so an entry cannot outlive the socket.
/// A <see cref="FakePresence"/> stands in for presence; the queue itself goes through the real
/// <c>ILiveLobbyGrain</c> on <see cref="LiveClusterFixture"/>'s silo.
/// </summary>
[Collection(nameof(LiveClusterCollection))]
public class LobbyHubTests(LiveClusterFixture fixture)
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
    private static int _n;

    private static Player RealPlayer(string id) => Player.Register(id, $"{id}@example.com", id, Language.En, T0);
    private static Player Guest(string id) => Player.Guest(id, id, Language.En, T0);

    private LobbyHubTestHost NewHost() => new(fixture.Cluster);

    private static Category SeedCategory(string suffix)
    {
        var id = $"lhb-cat-{suffix}-{Interlocked.Increment(ref _n)}";
        var category = new Category(id, $"دسته {suffix}", $"Category {suffix}", "globe", "#336699");
        LiveShared.Categories.Items.Add(category);

        for (var i = 0; i < 100; i++)
        {
            var qid = $"{id}-q{i}";
            LiveShared.Questions.Items.Add(Question.Create(qid, Language.En, id, MatchRules.LevelForSlot(i, 100),
                $"q {i}", ["right", "w1", "w2", "w3"], 0, LiveShared.TimeProvider.GetUtcNow(), status: QuestionStatus.Approved));
        }

        return category;
    }

    [Fact]
    public async Task Connecting_marks_the_player_online()
    {
        await using var host = NewHost();
        var token = host.TokenIssuer.Issue(RealPlayer("p1"));
        await using var connection = host.NewConnection(token);

        await connection.StartAsync();

        Assert.True(host.Presence.IsOnline("p1"));
    }

    [Fact]
    public async Task Disconnecting_marks_the_player_offline()
    {
        await using var host = NewHost();
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
        await using var host = NewHost();
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
        await using var host = NewHost();
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

    [Fact]
    public void The_live_queues_TTL_matches_the_presence_TTL()
    {
        Assert.Equal(LobbyHub.PresenceTtl, LiveLobbyGrain.Ttl);
    }

    [Fact]
    public async Task QueueRandom_over_the_hub_matches_two_real_players_into_one_duel()
    {
        var cat = SeedCategory("hub-match");
        var lang = (int)Language.En;
        var count = 1010 + (int)Interlocked.Increment(ref _n); // a bucket of this test's own

        await using var host = NewHost();
        var t1 = host.TokenIssuer.Issue(RealPlayer("lhb-p1-" + _n));
        var t2 = host.TokenIssuer.Issue(RealPlayer("lhb-p2-" + _n));
        await using var c1 = host.NewConnection(t1);
        await using var c2 = host.NewConnection(t2);
        await c1.StartAsync();
        await c2.StartAsync();

        var first = await c1.InvokeAsync<string?>("QueueRandom", lang, count, new List<string> { cat.Id }, new List<int>());
        Assert.Null(first);

        var second = await c2.InvokeAsync<string?>("QueueRandom", lang, count, new List<string> { cat.Id }, new List<int>());
        Assert.NotNull(second);
    }

    [Fact]
    public async Task LeaveQueue_over_the_hub_removes_the_callers_entry()
    {
        var cat = SeedCategory("hub-leave");
        var lang = (int)Language.En;
        var count = 2010 + (int)Interlocked.Increment(ref _n);

        await using var host = NewHost();
        var t1 = host.TokenIssuer.Issue(RealPlayer("lhb-lv1-" + _n));
        await using var c1 = host.NewConnection(t1);
        await c1.StartAsync();

        await c1.InvokeAsync("QueueRandom", lang, count, new List<string> { cat.Id }, new List<int>());
        await c1.InvokeAsync("LeaveQueue");

        var observer = fixture.Cluster.GrainFactory.GetGrain<ILiveLobbyGrain>(0);
        Assert.Equal(0, await observer.WaitingCountAsync("nobody", lang, count));
    }

    [Fact]
    public async Task Disconnecting_from_the_lobby_hub_dequeues_the_player()
    {
        var cat = SeedCategory("hub-disconnect");
        var lang = (int)Language.En;
        var count = 3010 + (int)Interlocked.Increment(ref _n);

        await using var host = NewHost();
        var t1 = host.TokenIssuer.Issue(RealPlayer("lhb-dc1-" + _n));
        var c1 = host.NewConnection(t1);
        await c1.StartAsync();
        await c1.InvokeAsync("QueueRandom", lang, count, new List<string> { cat.Id }, new List<int>());

        await c1.DisposeAsync();
        await Task.Delay(200); // OnDisconnectedAsync runs asynchronously after the client tears down

        var observer = fixture.Cluster.GrainFactory.GetGrain<ILiveLobbyGrain>(0);
        Assert.Equal(0, await observer.WaitingCountAsync("nobody", lang, count));
    }

    [Fact]
    public async Task A_guest_calling_QueueRandom_directly_is_refused_and_creates_no_entry_without_a_connection()
    {
        var grains = fixture.Cluster.GrainFactory;
        var presence = new FakePresence();
        var hub = new LobbyHub(presence, grains) { Context = new FakeHubCallerContext(Guest("lhb-guest").Id, isGuest: true) };
        var lang = (int)Language.En;
        var count = 4010 + (int)Interlocked.Increment(ref _n);

        await Assert.ThrowsAsync<HubException>(() => hub.QueueRandom(lang, count, [], []));

        var observer = grains.GetGrain<ILiveLobbyGrain>(0);
        Assert.Equal(0, await observer.WaitingCountAsync("nobody", lang, count));
    }

    [Fact]
    public async Task A_guest_calling_LeaveQueue_directly_is_refused_without_a_connection()
    {
        var grains = fixture.Cluster.GrainFactory;
        var presence = new FakePresence();
        var hub = new LobbyHub(presence, grains) { Context = new FakeHubCallerContext(Guest("lhb-guest2").Id, isGuest: true) };

        await Assert.ThrowsAsync<HubException>(() => hub.LeaveQueue());
    }
}
