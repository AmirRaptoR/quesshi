using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Quesshi.Domain;
using Quesshi.Server.Auth;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

/// <summary>
/// The one criterion that needs the real HTTP pipeline: a guest is denied by the endpoint group's
/// filter before any handler — and therefore any grain — ever runs. Everything else about
/// <c>LiveEndpoints</c> is covered driving the handlers directly in <see cref="LiveEndpointsTests"/>.
/// </summary>
[Collection(nameof(LiveClusterCollection))]
public class LiveEndpointsGuestGateTests(LiveClusterFixture fixture) : IAsyncDisposable
{
    private readonly LiveApiTestHost _host = new(fixture.Cluster);

    private HttpClient AuthedClient(Player player)
    {
        var client = _host.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _host.TokenIssuer.Issue(player));
        return client;
    }

    [Fact]
    public async Task A_guest_calling_POST_api_live_is_refused_with_403()
    {
        var guest = Player.Guest("gg-guest1", "Guest", Language.En, DateTimeOffset.UtcNow);
        using var client = AuthedClient(guest);

        var response = await client.PostAsJsonAsync("/api/live", new CreateMatchDto(false, "en", ["geography"], null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_signed_in_player_calling_POST_api_live_is_not_blocked_by_the_guest_gate()
    {
        var player = Player.Register("gg-player1", "gg-player1@example.com", "Amir", Language.En, DateTimeOffset.UtcNow);
        LiveShared.Players.Items.Add(player);
        using var client = AuthedClient(player);

        var response = await client.PostAsJsonAsync("/api/live", new CreateMatchDto(false, "en", ["geography"], null));

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();
}
