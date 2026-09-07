using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Server.Api;

namespace Quesshi.Server.Tests;

/// <summary>The friends list marks who is online — sourced straight from <see cref="IPresence"/>.</summary>
public class FriendsOfAsyncTests
{
    private sealed class FixedPresence(HashSet<string> online) : IPresence
    {
        public void Connected(string playerId, string connectionId) { }
        public void Disconnected(string playerId, string connectionId) { }
        public bool IsOnline(string playerId) => online.Contains(playerId);
    }

    [Fact]
    public async Task An_online_friend_is_marked_online_and_an_offline_one_is_not()
    {
        var players = new FakePlayers();
        var me = Player.Register("me", "me@example.com", "Me", Language.En, DateTimeOffset.UtcNow);
        var online = Player.Register("online-friend", "o@example.com", "Online", Language.En, DateTimeOffset.UtcNow);
        var offline = Player.Register("offline-friend", "f@example.com", "Offline", Language.En, DateTimeOffset.UtcNow);
        me.AddFriend(online.Id);
        me.AddFriend(offline.Id);
        players.Items.AddRange([me, online, offline]);

        var friends = await GameEndpoints.FriendsOfAsync(me, players, new FakeLeaderboard(), new FixedPresence([online.Id]));

        Assert.True(friends.Single(f => f.Id == online.Id).Online);
        Assert.False(friends.Single(f => f.Id == offline.Id).Online);
    }
}
