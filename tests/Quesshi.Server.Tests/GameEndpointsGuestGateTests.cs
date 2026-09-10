using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Quesshi.Domain;
using Quesshi.Server.Auth;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

/// <summary>
/// The two guest-gate criteria issue #104 adds to the async lobby's HTTP surface, driven against a
/// real endpoint pipeline (<see cref="GameApiTestHost"/>) rather than by calling <c>GameEndpoints</c>'
/// handlers directly, the same reason <see cref="ProfileEndpointGuestTests"/> and
/// <see cref="LiveEndpointsGuestGateTests"/> both do: the guest gate is an endpoint filter that runs
/// before any handler, and a direct handler call would skip it entirely.
/// </summary>
[Collection(nameof(ClusterCollection))]
public class GameEndpointsGuestGateTests(ClusterFixture fixture) : IAsyncDisposable
{
    private readonly GameApiTestHost _host = new(fixture.Cluster);

    private HttpClient AuthedClient(Player player)
    {
        var client = _host.NewClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _host.TokenIssuer.Issue(player));
        return client;
    }

    private async Task<string> CreateLobbyCodeAsync()
    {
        var owner = Player.Register($"gg-owner-{Guid.NewGuid():N}", $"gg-{Guid.NewGuid():N}@example.com", "Owner", Language.En, DateTimeOffset.UtcNow);
        Shared.Players.Items.Add(owner);
        using var client = AuthedClient(owner);

        var response = await client.PostAsJsonAsync("/api/matches/lobby", new CreateLobbyDto(3, "en", ["geography"], null, null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lobby = await response.Content.ReadFromJsonAsync<MatchSummaryDto>();
        return lobby!.Code;
    }

    [Fact]
    public async Task A_guest_reading_a_lobby_by_code_gets_200_not_403()
    {
        var code = await CreateLobbyCodeAsync();
        var guest = Player.Guest($"gg-guest-{Guid.NewGuid():N}", "Guest", Language.En, DateTimeOffset.UtcNow);
        using var client = AuthedClient(guest);

        var response = await client.GetAsync($"/api/matches/by-code/{code}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_guest_joining_a_lobby_by_code_gets_200_not_403()
    {
        var code = await CreateLobbyCodeAsync();
        var guest = Player.Guest($"gg-guest-{Guid.NewGuid():N}", "Guest", Language.En, DateTimeOffset.UtcNow);
        Shared.Players.Items.Add(guest);
        using var client = AuthedClient(guest);

        var response = await client.PostAsync($"/api/matches/join/{code}", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();
}
