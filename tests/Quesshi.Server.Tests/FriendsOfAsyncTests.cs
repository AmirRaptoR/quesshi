using Quesshi.Domain;
using Quesshi.Server.Api;

namespace Quesshi.Server.Tests;

/// <summary>
/// The friends list marks who is online, computed from a single <see cref="FakePresence"/> call
/// regardless of how many friends there are — no grain, no Mongo, no Redis needed.
/// </summary>
public class FriendsOfAsyncTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static Player NewPlayer(string id) => Player.Register(id, $"{id}@example.com", id, Language.En, T0);

    [Fact]
    public async Task Online_friends_are_marked_online_and_offline_friends_are_not_from_a_single_presence_call()
    {
        var players = new FakePlayers();
        var board = new FakeLeaderboard();
        var presence = new FakePresence();

        var me = NewPlayer("me");
        var online1 = NewPlayer("f-online-1");
        var online2 = NewPlayer("f-online-2");
        var online3 = NewPlayer("f-online-3");
        var offline1 = NewPlayer("f-offline-1");
        var offline2 = NewPlayer("f-offline-2");
        foreach (var friend in new[] { online1, online2, online3, offline1, offline2 })
        {
            me.AddFriend(friend.Id);
            players.Items.Add(friend);
        }
        players.Items.Add(me);
        await presence.MarkOnlineAsync("f-online-1", TimeSpan.FromMinutes(1));
        await presence.MarkOnlineAsync("f-online-2", TimeSpan.FromMinutes(1));
        await presence.MarkOnlineAsync("f-online-3", TimeSpan.FromMinutes(1));

        var friends = await GameEndpoints.FriendsOfAsync(me, players, board, presence);

        Assert.Equal(1, presence.OnlineCalls);
        Assert.True(friends.Single(f => f.Id == "f-online-1").Online);
        Assert.True(friends.Single(f => f.Id == "f-online-2").Online);
        Assert.True(friends.Single(f => f.Id == "f-online-3").Online);
        Assert.False(friends.Single(f => f.Id == "f-offline-1").Online);
        Assert.False(friends.Single(f => f.Id == "f-offline-2").Online);
    }

    [Fact]
    public async Task A_player_with_no_friends_makes_no_presence_call_at_all()
    {
        var players = new FakePlayers();
        var presence = new FakePresence();
        var me = NewPlayer("me");
        players.Items.Add(me);

        var friends = await GameEndpoints.FriendsOfAsync(me, players, new FakeLeaderboard(), presence);

        Assert.Empty(friends);
        Assert.Equal(0, presence.OnlineCalls);
    }
}
