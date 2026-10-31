using System.Net;
using System.Net.Http.Json;
using Quesshi.Features.Features.Friends;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

public sealed class FriendsSearchEndpointDatabaseTests : IAsyncLifetime
{
    private static readonly bool IsPostgresConfigured = PostgresFixture.IsConfigured(
        Environment.GetEnvironmentVariable(PostgresFixture.AdminConnectionStringEnvironmentVariable),
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase));
    private PlatformHttpFixture? _host;

    public async Task InitializeAsync()
    {
        if (!IsPostgresConfigured) return;

        _host = new PlatformHttpFixture([new FriendsFeature()]);
        await _host.StartAsync();
        await _host.SetCapabilityAsync("feature", "friends", true);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task Search_returns_a_tenant_player_matching_the_display_name()
    {
        var id = await SeedPlayerAsync(Host.TenantId, "Zanzibar Quill");
        await SeedPlayerAsync(Host.TenantId, "Unrelated Person");

        var found = await SearchAsync("zanzib");

        Assert.Equal([new FriendSearchCandidate(id, "Zanzibar Quill", "seed")], found);
    }

    [SkippableFact]
    public async Task Search_matches_an_email_without_disclosing_it()
    {
        var id = await SeedPlayerAsync(Host.TenantId, "Plain Name", email: "hidden.handle@mail.test");

        using var response = await Host.Player.GetAsync("/api/x/friends/search?q=hidden.handle");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(id.ToString(), body);
        Assert.DoesNotContain("hidden.handle", body);
        Assert.DoesNotContain("mail.test", body);
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("z")]
    [InlineData("  z  ")]
    [InlineData("    ")]
    public async Task Search_with_fewer_than_two_trimmed_characters_returns_nothing(string query)
    {
        await SeedPlayerAsync(Host.TenantId, "Zed Match");

        Assert.Empty(await SearchAsync(query));
    }

    [SkippableFact]
    public async Task Search_omits_a_guest()
    {
        var (_, guestId) = await Host.CreateAnotherGuestClientAsync();
        await Host.ExecuteOwnerAsync("update players set display_name = 'Kiwi Guest' where tenant_id = @t and id = @id", new { t = Host.TenantId, id = guestId });
        var eligible = await SeedPlayerAsync(Host.TenantId, "Kiwi Real");

        Assert.Equal([eligible], (await SearchAsync("kiwi")).Select(static c => c.Id));
    }

    [SkippableFact]
    public async Task Search_omits_a_banned_player()
    {
        var banned = await SeedPlayerAsync(Host.TenantId, "Mango Banned");
        await Host.ExecuteOwnerAsync("update players set banned_at = now() where tenant_id = @t and id = @id", new { t = Host.TenantId, id = banned });
        var eligible = await SeedPlayerAsync(Host.TenantId, "Mango Fine");

        Assert.Equal([eligible], (await SearchAsync("mango")).Select(static c => c.Id));
    }

    [SkippableFact]
    public async Task Search_omits_a_deleted_player()
    {
        var deleted = await SeedPlayerAsync(Host.TenantId, "Plum Deleted");
        await Host.ExecuteOwnerAsync("update players set deleted_at = now() where tenant_id = @t and id = @id", new { t = Host.TenantId, id = deleted });
        var eligible = await SeedPlayerAsync(Host.TenantId, "Plum Fine");

        Assert.Equal([eligible], (await SearchAsync("plum")).Select(static c => c.Id));
    }

    [SkippableFact]
    public async Task Search_omits_a_matching_player_from_another_tenant()
    {
        var (_, otherId) = await Host.CreateOtherTenantPlayerWithIdAsync();
        await Host.ExecuteOwnerAsync("update players set display_name = 'Fig Other' where id = @id", new { id = otherId });
        var eligible = await SeedPlayerAsync(Host.TenantId, "Fig Local");

        Assert.Equal([eligible], (await SearchAsync("fig")).Select(static c => c.Id));
    }

    [SkippableFact]
    public async Task Search_omits_the_searching_player()
    {
        await Host.ExecuteOwnerAsync("update players set display_name = 'Lime Self' where tenant_id = @t and id = @id", new { t = Host.TenantId, id = Host.PlayerId });
        var eligible = await SeedPlayerAsync(Host.TenantId, "Lime Other");

        Assert.Equal([eligible], (await SearchAsync("lime")).Select(static c => c.Id));
    }

    [SkippableFact]
    public async Task Search_omits_an_existing_friend()
    {
        var friend = await SeedPlayerAsync(Host.TenantId, "Pear Friend");
        var eligible = await SeedPlayerAsync(Host.TenantId, "Pear Stranger");
        await Host.ExecuteOwnerAsync("""
            insert into friendships (tenant_id, player_id, friend_id)
            values (@t, @a, @b), (@t, @b, @a)
            """, new { t = Host.TenantId, a = Host.PlayerId, b = friend });

        Assert.Equal([eligible], (await SearchAsync("pear")).Select(static c => c.Id));
    }

    [SkippableFact]
    public async Task Search_returns_at_most_twenty_candidates()
    {
        for (var i = 0; i < 25; i++) await SeedPlayerAsync(Host.TenantId, $"Berry {i:00}");

        Assert.Equal(20, (await SearchAsync("berry")).Count);
    }

    [SkippableFact]
    public async Task Search_treats_wildcards_literally()
    {
        await SeedPlayerAsync(Host.TenantId, "Wild Card");

        Assert.Empty(await SearchAsync("%%"));
    }

    [SkippableFact]
    public async Task Guest_is_refused_by_search()
    {
        using var response = await Host.Guest.GetAsync("/api/x/friends/search?q=anything");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [SkippableFact]
    public async Task Search_is_unavailable_when_friends_is_disabled()
    {
        await Host.SetCapabilityAsync("feature", "friends", false);

        using var response = await Host.Player.GetAsync("/api/x/friends/search?q=anything");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private PlatformHttpFixture Host
    {
        get
        {
            Skip.If(!IsPostgresConfigured, "PostgreSQL is not configured.");
            return _host ?? throw new InvalidOperationException("Platform host did not start.");
        }
    }

    private async Task<IReadOnlyList<FriendSearchCandidate>> SearchAsync(string query)
    {
        using var response = await Host.Player.GetAsync($"/api/x/friends/search?q={Uri.EscapeDataString(query)}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<FriendSearchCandidate>>() ?? [];
    }

    private async Task<Guid> SeedPlayerAsync(Guid tenantId, string displayName, string? email = null)
    {
        var accountId = Guid.NewGuid();
        var playerId = Guid.NewGuid();
        await Host.ExecuteOwnerAsync("""
            insert into accounts(id, email) values (@accountId, @email);
            insert into players(tenant_id, id, account_id, display_name, avatar_seed, lang)
            values (@tenantId, @playerId, @accountId, @displayName, 'seed', 'en');
            """, new { tenantId, accountId, playerId, displayName, email = email ?? $"seed-{accountId:N}@example.test" });
        return playerId;
    }
}
