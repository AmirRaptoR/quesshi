using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;
using Quesshi.Server.Auth;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

/// <summary>The dashboard's new live row: in-flight count, connected players, queue depth, the
/// live-duels total beside <c>Matches</c>, and the current <c>Live:Enabled</c> value.</summary>
[Collection(nameof(LiveClusterCollection))]
public class AdminDashboardLiveTests(LiveClusterFixture fixture) : IAsyncDisposable
{
    private readonly AdminApiTestHost _host = new(fixture.Cluster);

    private HttpClient AdminClient()
    {
        var client = _host.NewClient();
        var admin = AdminUser.Create($"adl-admin-{Guid.NewGuid():N}", "admin", "admin@example.com", "hash", DateTimeOffset.UtcNow);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _host.AdminTokenIssuer.Issue(admin));
        return client;
    }

    [Fact]
    public async Task Renders_with_every_live_number_at_zero_and_live_enabled()
    {
        LiveShared.Directory.Rows.Clear();
        LiveShared.Directory.ConnectedCount = 0;
        LiveShared.Directory.QueueDepth = 0;
        LiveShared.Archive.Items.RemoveAll(m => m.IsLive);
        await fixture.Cluster.GrainFactory.GetGrain<ILiveSettingsGrain>(0).SetEnabledAsync(true);

        using var client = AdminClient();
        var dto = await client.GetFromJsonAsync<AdminDashboardDto>("/api/admin/dashboard");

        Assert.Equal(0, dto!.LiveInFlight);
        Assert.Equal(0, dto.PlayersConnected);
        Assert.Equal(0, dto.QueueDepth);
        Assert.Equal(0, dto.LiveMatches);
        Assert.True(dto.LiveEnabled);
    }

    [Fact]
    public async Task Reports_the_in_flight_count_the_connected_and_queue_counters_and_the_live_total()
    {
        LiveShared.Directory.Rows.Clear();
        LiveShared.Directory.Rows["adl-row"] = new LiveDirectoryRow("adl-row", "ADLROW", "p1", "p2",
            (int)Language.En, 0, 10, (int)LivePhase.Lobby, DateTimeOffset.UtcNow);
        LiveShared.Directory.ConnectedCount = 7;
        LiveShared.Directory.QueueDepth = 3;
        LiveShared.Archive.Items.Add(new ArchivedMatch("adl-match", "ADLM01", Language.En, "p1", "p2", null, false,
            FakeArchive.TestResults("p1", "p2", 0, 0), MatchState.InProgress, DateTimeOffset.UtcNow, null, [], IsLive: true));

        using var client = AdminClient();
        var dto = await client.GetFromJsonAsync<AdminDashboardDto>("/api/admin/dashboard");

        Assert.Equal(1, dto!.LiveInFlight);
        Assert.Equal(7, dto.PlayersConnected);
        Assert.Equal(3, dto.QueueDepth);
        Assert.True(dto.LiveMatches >= 1);

        LiveShared.Directory.Rows.Clear();
        LiveShared.Directory.ConnectedCount = 0;
        LiveShared.Directory.QueueDepth = 0;
        LiveShared.Archive.Items.RemoveAll(m => m.Id == "adl-match");
    }

    [Fact]
    public async Task Renders_with_live_disabled()
    {
        await fixture.Cluster.GrainFactory.GetGrain<ILiveSettingsGrain>(0).SetEnabledAsync(false);
        try
        {
            using var client = AdminClient();
            var response = await client.GetAsync("/api/admin/dashboard");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var dto = await response.Content.ReadFromJsonAsync<AdminDashboardDto>();
            Assert.False(dto!.LiveEnabled);
        }
        finally
        {
            await fixture.Cluster.GrainFactory.GetGrain<ILiveSettingsGrain>(0).SetEnabledAsync(true);
        }
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();
}
