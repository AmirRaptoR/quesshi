using System.Net;
using System.Net.Http.Json;
using Quesshi.Features.Features.Friends;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

public sealed class SessionInviteEligibilityDatabaseTests : IAsyncLifetime
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
    public async Task Friends_enabled_accepts_a_mutual_friend_who_is_not_a_recent_co_player()
    {
        var (_, friendId) = await Host.CreateSecondPlayerClientAsync();
        await MakeFriendsAsync(Host.PlayerId, friendId);
        var sessionId = await SeedLobbyAsync();

        using var response = await InviteAsync(sessionId, friendId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await InviteCountAsync(sessionId, friendId));
    }

    [SkippableFact]
    public async Task Friends_enabled_refuses_a_player_who_is_neither_a_friend_nor_a_recent_co_player()
    {
        var (_, strangerId) = await Host.CreateSecondPlayerClientAsync();
        var sessionId = await SeedLobbyAsync();

        using var response = await InviteAsync(sessionId, strangerId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await InviteCountAsync(sessionId, strangerId));
    }

    [SkippableFact]
    public async Task Friends_disabled_refuses_a_friend_who_is_not_a_recent_co_player()
    {
        var (_, friendId) = await Host.CreateSecondPlayerClientAsync();
        await MakeFriendsAsync(Host.PlayerId, friendId);
        await Host.SetCapabilityAsync("feature", "friends", false);
        var sessionId = await SeedLobbyAsync();

        using var response = await InviteAsync(sessionId, friendId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await InviteCountAsync(sessionId, friendId));
    }

    [SkippableFact]
    public async Task Friends_disabled_still_accepts_a_recent_co_player()
    {
        var (_, coPlayerId) = await Host.CreateSecondPlayerClientAsync();
        await Host.SetCapabilityAsync("feature", "friends", false);
        var earlier = await SeedLobbyAsync();
        await SeatAsync(earlier, coPlayerId, seat: 1);
        var sessionId = await SeedLobbyAsync();

        using var response = await InviteAsync(sessionId, coPlayerId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await InviteCountAsync(sessionId, coPlayerId));
    }

    private PlatformHttpFixture Host => _host ?? throw new InvalidOperationException("The platform host is not configured.");

    private Task<HttpResponseMessage> InviteAsync(Guid sessionId, Guid playerId) =>
        Host.Player.PostAsJsonAsync($"/api/sessions/{sessionId}/invites", new { playerId });

    private async Task<Guid> SeedLobbyAsync()
    {
        var sessionId = Guid.NewGuid();
        await Host.ExecuteOwnerAsync("""
            insert into sessions(tenant_id, id, code, format, owner_id, lang, status, capacity, content)
            values (@tenantId, @sessionId, @code, 'async_duel', @ownerId, 'en', 'lobby', 4, '{}'::jsonb)
            """, new { tenantId = Host.TenantId, sessionId, code = $"INV{sessionId:N}"[..12], ownerId = Host.PlayerId });
        await SeatAsync(sessionId, Host.PlayerId, seat: 0);
        return sessionId;
    }

    private Task SeatAsync(Guid sessionId, Guid playerId, int seat) => Host.ExecuteOwnerAsync("""
        insert into session_participants(tenant_id, session_id, player_id, seat, role, status, phase, slot, step)
        values (@tenantId, @sessionId, @playerId, @seat, 'player', 'active', 'idle', -1, 0)
        """, new { tenantId = Host.TenantId, sessionId, playerId, seat });

    private Task MakeFriendsAsync(Guid first, Guid second) => Host.ExecuteOwnerAsync("""
        insert into friendships(tenant_id, player_id, friend_id)
        values (@tenantId, @first, @second), (@tenantId, @second, @first)
        """, new { tenantId = Host.TenantId, first, second });

    private async Task<int> InviteCountAsync(Guid sessionId, Guid playerId) =>
        (await Host.QueryOwnerAsync<int>(
            "select count(*)::int from session_invites where session_id = @sessionId and player_id = @playerId",
            new { sessionId, playerId })).Single();
}
