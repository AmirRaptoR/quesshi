using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;
using Quesshi.Server.Auth;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

[Collection(nameof(LiveClusterCollection))]
public sealed class AdminLobbySettingsTests(LiveClusterFixture fixture) : IAsyncDisposable
{
    private readonly AdminApiTestHost _host = new(fixture.Cluster);

    private HttpClient AdminClient()
    {
        var client = _host.NewClient();
        var admin = AdminUser.Create($"capacity-{Guid.NewGuid():N}", "admin", "admin@example.com", "hash", DateTimeOffset.UtcNow);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _host.AdminTokenIssuer.Issue(admin));
        return client;
    }

    [Fact]
    public async Task Dashboard_reads_the_saved_value_and_admin_can_update_it()
    {
        var settings = fixture.Cluster.GrainFactory.GetGrain<ILobbySettingsGrain>(0);
        await settings.SetMaxCapacityAsync(MatchRules.DefaultMaxParticipants);
        using var client = AdminClient();

        var initial = await client.GetFromJsonAsync<AdminDashboardDto>("/api/admin/dashboard");
        Assert.Equal(20, initial!.MaxLobbyCapacity);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/admin/lobby/max-capacity?value=500", null)).StatusCode);
        Assert.Equal(500, await settings.GetMaxCapacityAsync());

        await settings.SetMaxCapacityAsync(MatchRules.DefaultMaxParticipants);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("2.5")]
    [InlineData("1")]
    [InlineData("501")]
    public async Task Invalid_admin_values_are_rejected_without_changing_the_setting(string value)
    {
        var settings = fixture.Cluster.GrainFactory.GetGrain<ILobbySettingsGrain>(0);
        await settings.SetMaxCapacityAsync(20);
        using var client = AdminClient();

        var response = await client.PostAsync($"/api/admin/lobby/max-capacity?value={value}", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(20, await settings.GetMaxCapacityAsync());
    }

    [Fact]
    public async Task Non_admin_cannot_read_or_mutate_the_setting()
    {
        using var client = _host.NewClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsync("/api/admin/lobby/max-capacity?value=30", null)).StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        await fixture.Cluster.GrainFactory.GetGrain<ILobbySettingsGrain>(0)
            .SetMaxCapacityAsync(MatchRules.DefaultMaxParticipants);
        await _host.DisposeAsync();
    }
}
