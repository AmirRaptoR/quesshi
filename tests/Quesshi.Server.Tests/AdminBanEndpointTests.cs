using System.Net;
using System.Net.Http.Headers;
using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;
using Quesshi.Server.Auth;

namespace Quesshi.Server.Tests;

/// <summary>
/// <c>POST /admin/users/{id}/ban</c> at the real HTTP layer (issue #54): the endpoint itself now only
/// reads the repository to shape its 404/400 responses and calls <see cref="IPlayerGrain.SetBannedAsync"/>
/// for the actual write, so this proves the wiring end to end — the grain-level proof that the write
/// survives a later settlement lives in <see cref="PlayerGrainWriteOwnershipTests"/>.
/// </summary>
[Collection(nameof(LiveClusterCollection))]
public class AdminBanEndpointTests(LiveClusterFixture fixture) : IAsyncDisposable
{
    private readonly AdminApiTestHost _host = new(fixture.Cluster);

    private HttpClient AdminClient()
    {
        var client = _host.NewClient();
        var admin = AdminUser.Create($"abe-admin-{Guid.NewGuid():N}", "admin", "admin@example.com", "hash", DateTimeOffset.UtcNow);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _host.AdminTokenIssuer.Issue(admin));
        return client;
    }

    private static Player NewPlayer()
    {
        var id = $"abe-player-{Guid.NewGuid():N}";
        var player = Player.Register(id, $"{id}@example.com", "Amir", Language.En, DateTimeOffset.UtcNow);
        LiveShared.Players.Items.Add(player);
        return player;
    }

    [Fact]
    public async Task Banning_a_player_persists_through_the_grain()
    {
        var player = NewPlayer();
        using var client = AdminClient();

        var response = await client.PostAsync($"/api/admin/users/{player.Id}/ban?value=true", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await LiveShared.Players.GetAsync(player.Id))!.IsBanned);
    }

    [Fact]
    public async Task Banning_an_unknown_player_is_404()
    {
        using var client = AdminClient();

        var response = await client.PostAsync($"/api/admin/users/abe-nobody-{Guid.NewGuid():N}/ban?value=true", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>The actual bug, proved through the endpoint rather than the grain directly: a ban
    /// applied over HTTP must still be standing after a duel for that player settles.</summary>
    [Fact]
    public async Task A_settled_match_does_not_lift_an_HTTP_applied_ban()
    {
        var player = NewPlayer();
        using var client = AdminClient();

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/admin/users/{player.Id}/ban?value=true", null)).StatusCode);

        await fixture.Cluster.GrainFactory.GetGrain<IPlayerGrain>(player.Id)
            .SettleMatchAsync($"abe-m-{Guid.NewGuid():N}", (int)MatchOutcome.Loss, 5, [], [], null);

        Assert.True((await LiveShared.Players.GetAsync(player.Id))!.IsBanned);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();
}
