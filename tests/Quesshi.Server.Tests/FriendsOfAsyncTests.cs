using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Server.Api;

namespace Quesshi.Server.Tests;

/// <summary>The friends list marks who is online — sourced straight from <see cref="IPresence"/>.</summary>
public class FriendsOfAsyncTests
{
    private sealed class FixedPresence(HashSet<string> online) : IPresence
    {
        public Task MarkOnlineAsync(string playerId, TimeSpan ttl, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkOfflineAsync(string playerId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyCollection<string>> OnlineAsync(IReadOnlyCollection<string> playerIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyCollection<string>>([.. playerIds.Where(online.Contains)]);
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
