using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Plugin.Api;
using Quesshi.Server.Streaming;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

public sealed class PlayerOperationsEndpointTests : IAsyncLifetime
{
    private static readonly bool IsPostgresConfigured = PostgresFixture.IsConfigured(
        Environment.GetEnvironmentVariable(PostgresFixture.AdminConnectionStringEnvironmentVariable),
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase));
    private PlatformHttpFixture? host;

    public async Task InitializeAsync()
    {
        if (!IsPostgresConfigured) return;
        host = new PlatformHttpFixture([new PlayerOperationsHttpFeature()]);
        await host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (host is not null) await host.DisposeAsync();
    }

    [SkippableFact]
    public async Task Moderator_can_page_through_tenant_players()
    {
        var player = Guid.NewGuid();
        await Host.ExecuteOwnerAsync("""
            insert into players(tenant_id, id, account_id, display_name, avatar_seed, lang)
            values (@tenantId, @player, null, 'Paged player', 'paged-player', 'en');
            """, new { tenantId = Host.TenantId, player });

        using var response = await Host.Moderator.GetAsync("/api/admin/players?page=2&pageSize=2");
        var page = await response.Content.ReadFromJsonAsync<PlayerPage>();

        response.EnsureSuccessStatusCode();
        Assert.Equal(3, page?.Total);
        Assert.Equal(2, page?.Page);
        Assert.Equal(2, page?.PageSize);
        Assert.Contains(page?.Items ?? [], item => item.Id == player);
    }

    [SkippableFact]
    public async Task Moderator_search_returns_the_matching_tenant_player()
    {
        var player = Guid.NewGuid();
        await Host.ExecuteOwnerAsync("""
            insert into players(tenant_id, id, account_id, display_name, avatar_seed, lang)
            values (@tenantId, @player, null, 'Searchable player', 'searchable-player', 'en');
            """, new { tenantId = Host.TenantId, player });

        using var response = await Host.Moderator.GetAsync("/api/admin/players?q=Searchable%20player");
        var page = await response.Content.ReadFromJsonAsync<PlayerPage>();

        response.EnsureSuccessStatusCode();
        Assert.Contains(page?.Items ?? [], item => item.Id == player && item.DisplayName == "Searchable player");
    }

    [SkippableFact]
    public async Task Player_search_excludes_a_plausible_matching_player_from_another_tenant()
    {
        var localPlayer = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var otherPlayer = Guid.NewGuid();
        await Host.ExecuteOwnerAsync("""
            insert into tenants(id, slug, name, default_language)
            values (@otherTenant, @slug, 'Other player operations tenant', 'en');
            insert into players(tenant_id, id, account_id, display_name, avatar_seed, lang)
            values (@tenantId, @localPlayer, null, 'Shared display name', 'local-shared', 'en'),
                   (@otherTenant, @otherPlayer, null, 'Shared display name', 'foreign-shared', 'en');
            """, new { tenantId = Host.TenantId, localPlayer, otherTenant, otherPlayer, slug = $"other-operations-{otherTenant:N}" });

        using var response = await Host.Moderator.GetAsync("/api/admin/players?q=Shared%20display%20name");
        var page = await response.Content.ReadFromJsonAsync<PlayerPage>();

        response.EnsureSuccessStatusCode();
        Assert.Contains(page?.Items ?? [], item => item.Id == localPlayer);
        Assert.DoesNotContain(page?.Items ?? [], item => item.Id == otherPlayer);
    }

    [SkippableFact]
    public async Task Banning_a_player_from_the_admin_route_closes_the_open_stream()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var streamResponseTask = Host.OpenPlayerStreamAsync(timeout.Token);
        await WaitForSubscriptionAsync(Host.Services.GetRequiredService<StreamHub>(), timeout.Token);
        await Host.ExecuteOwnerAsync("select pg_notify('quesshi_config', @message)",
            new { message = $"config:{Host.TenantId}:1" }, timeout.Token);
        using var streamResponse = await streamResponseTask;
        streamResponse.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await streamResponse.Content.ReadAsStreamAsync());
        Assert.Contains("event: config", await ReadFrameAsync(reader, timeout.Token), StringComparison.Ordinal);

        using var banResponse = await Host.Moderator.PostAsync($"/api/admin/players/{Host.PlayerId}/ban", null);
        banResponse.EnsureSuccessStatusCode();
        var frame = await ReadUntilFrameAsync(reader, "event: closed", timeout.Token);

        Assert.Contains("event: closed\ndata: {\"reason\":\"banned\"}", frame, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Banned_player_is_refused_on_its_next_protected_request()
    {
        using var banResponse = await Host.Moderator.PostAsync($"/api/admin/players/{Host.PlayerId}/ban", null);
        banResponse.EnsureSuccessStatusCode();

        using var response = await Host.Player.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [SkippableFact]
    public async Task Unbanning_a_player_restores_protected_access_with_a_new_session()
    {
        using (var banResponse = await Host.Moderator.PostAsync($"/api/admin/players/{Host.PlayerId}/ban", null))
            banResponse.EnsureSuccessStatusCode();
        using (var unbanResponse = await Host.Moderator.DeleteAsync($"/api/admin/players/{Host.PlayerId}/ban"))
            unbanResponse.EnsureSuccessStatusCode();
        using var restored = await Host.CreateFreshPlayerClientAsync();
        using var response = await restored.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [SkippableFact]
    public async Task Player_operations_allow_moderators_and_reject_editors()
    {
        await RoleMatrix.ForModeratorOnlyOperation(Host)
            .AssertAsync(client => client.GetAsync("/api/admin/players"));
    }

    private static async Task<string> ReadFrameAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var frame = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null || line.Length == 0) return frame.ToString();
            frame.AppendLine(line);
        }
    }

    private static async Task<string> ReadUntilFrameAsync(StreamReader reader, string requiredContent, CancellationToken cancellationToken)
    {
        while (true)
        {
            var frame = await ReadFrameAsync(reader, cancellationToken);
            if (frame.Contains(requiredContent, StringComparison.Ordinal)) return frame;
        }
    }

    private static async Task WaitForSubscriptionAsync(StreamHub hub, CancellationToken cancellationToken)
    {
        while (hub.SubscriptionCount != 1)
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
    }

    private PlatformHttpFixture Host
    {
        get
        {
            Skip.If(!IsPostgresConfigured, "PostgreSQL is not configured.");
            return host ?? throw new InvalidOperationException("The platform HTTP host did not start.");
        }
    }

    private sealed record PlayerPage(IReadOnlyList<PlayerItem> Items, long Total, int Page, int PageSize);
    private sealed record PlayerItem(Guid Id, string DisplayName, bool IsBanned);

    private sealed class PlayerOperationsHttpFeature : IFeature
    {
        public string Id => "player_operations_http";
        public FeatureSettings Settings => FeatureSettings.Create(new PlayerOperationsSettings(), _ => []);
        public void Configure(IFeatureBuilder feature) { }
    }

    private sealed record PlayerOperationsSettings;
}
