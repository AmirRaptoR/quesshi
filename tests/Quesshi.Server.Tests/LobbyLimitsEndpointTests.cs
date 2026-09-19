using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

[Collection(nameof(ClusterCollection))]
public sealed class LobbyLimitsEndpointTests(ClusterFixture fixture) : IAsyncDisposable
{
    private readonly GameApiTestHost _host = new(fixture.Cluster);

    [Fact]
    public async Task Authenticated_guest_can_read_the_current_limit_without_admin_access()
    {
        var settings = fixture.Cluster.GrainFactory.GetGrain<ILobbySettingsGrain>(0);
        await settings.SetMaxCapacityAsync(42);
        try
        {
            using var client = _host.NewClient();
            var guest = Player.Guest($"limits-{Guid.NewGuid():N}", "Guest", Language.En, DateTimeOffset.UtcNow);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _host.TokenIssuer.Issue(guest));

            var response = await client.GetAsync("/api/lobby-limits");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(42, (await response.Content.ReadFromJsonAsync<LobbyLimitsDto>())!.MaxCapacity);
        }
        finally
        {
            await settings.SetMaxCapacityAsync(MatchRules.DefaultMaxParticipants);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await fixture.Cluster.GrainFactory.GetGrain<ILobbySettingsGrain>(0)
            .SetMaxCapacityAsync(MatchRules.DefaultMaxParticipants);
        await _host.DisposeAsync();
    }
}
