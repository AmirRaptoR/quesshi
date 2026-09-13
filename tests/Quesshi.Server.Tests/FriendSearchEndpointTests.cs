using System.Net.Http.Headers;
using System.Net.Http.Json;
using Orleans.TestingHost;
using Quesshi.Domain;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

[Collection(nameof(ClusterCollection))]
public sealed class FriendSearchEndpointTests(ClusterFixture fixture) : IAsyncDisposable
{
    private readonly GameApiTestHost _host = new(fixture.Cluster);

    [Fact]
    public async Task Search_finds_an_account_by_email_without_exposing_email_or_existing_friends()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var me = Player.Register($"friend-search-me-{suffix}", $"me-{suffix}@example.com",
            "Search owner", Language.En, Shared.Clock.Now);
        var candidate = Player.Register($"friend-search-target-{suffix}", $"known-{suffix}@example.com",
            "Different display name", Language.En, Shared.Clock.Now);
        var existing = Player.Register($"friend-search-existing-{suffix}", $"existing-{suffix}@example.com",
            $"known-{suffix}", Language.En, Shared.Clock.Now);
        var guest = Player.Guest($"friend-search-guest-{suffix}", $"known-{suffix}",
            Language.En, Shared.Clock.Now);
        me.AddFriend(existing.Id);
        Shared.Players.Items.AddRange([me, candidate, existing, guest]);

        using var client = _host.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _host.TokenIssuer.Issue(me));

        var rows = await client.GetFromJsonAsync<List<FriendDto>>(
            $"/api/players/search?q=known-{suffix}");

        var found = Assert.Single(rows!);
        Assert.Equal(candidate.Id, found.Id);
        Assert.Equal(candidate.DisplayName, found.DisplayName);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();
}
