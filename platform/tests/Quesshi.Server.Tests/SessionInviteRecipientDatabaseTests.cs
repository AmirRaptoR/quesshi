using System.Net;
using System.Net.Http.Json;
using Quesshi.Features.Features.Friends;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

public sealed class SessionInviteRecipientDatabaseTests : IAsyncLifetime
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
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task Accepting_an_invitation_seats_the_recipient_and_consumes_it()
    {
        var (client, playerId) = await Host.CreateSecondPlayerClientAsync();
        var sessionId = await SeedInvitedLobbyAsync(playerId, "now() + interval '1 hour'");

        using var response = await client.PostAsync($"/api/sessions/{sessionId}/invites/accept", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await SeatCountAsync(sessionId, playerId));
        Assert.Equal(0, await InviteCountAsync(sessionId, playerId));
    }

    [SkippableFact]
    public async Task Declining_an_invitation_removes_it_without_seating_the_recipient()
    {
        var (client, playerId) = await Host.CreateSecondPlayerClientAsync();
        var sessionId = await SeedInvitedLobbyAsync(playerId, "now() + interval '1 hour'");

        using var response = await client.PostAsync($"/api/sessions/{sessionId}/invites/decline", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await SeatCountAsync(sessionId, playerId));
        Assert.Equal(0, await InviteCountAsync(sessionId, playerId));
    }

    [SkippableFact]
    public async Task Accepting_an_expired_invitation_is_refused_without_seating()
    {
        var (client, playerId) = await Host.CreateSecondPlayerClientAsync();
        var sessionId = await SeedInvitedLobbyAsync(playerId, "now() - interval '1 minute'");

        using var response = await client.PostAsync($"/api/sessions/{sessionId}/invites/accept", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await SeatCountAsync(sessionId, playerId));
    }

    [SkippableFact]
    public async Task A_different_player_cannot_accept_the_recipients_invitation()
    {
        var (_, recipientId) = await Host.CreateSecondPlayerClientAsync();
        var (otherClient, otherId) = await Host.CreateSecondPlayerClientAsync();
        var sessionId = await SeedInvitedLobbyAsync(recipientId, "now() + interval '1 hour'");

        using var response = await otherClient.PostAsync($"/api/sessions/{sessionId}/invites/accept", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await SeatCountAsync(sessionId, otherId));
        Assert.Equal(0, await SeatCountAsync(sessionId, recipientId));
        Assert.Equal(1, await InviteCountAsync(sessionId, recipientId));
    }

    [SkippableFact]
    public async Task A_consumed_invitation_is_absent_from_the_next_inbox_snapshot()
    {
        var (client, playerId) = await Host.CreateSecondPlayerClientAsync();
        var sessionId = await SeedInvitedLobbyAsync(playerId, "now() + interval '1 hour'");
        Assert.Contains(sessionId.ToString(), await client.GetStringAsync("/api/me/inbox"), StringComparison.OrdinalIgnoreCase);
        var before = await InboxVersionAsync(playerId);

        using var response = await client.PostAsync($"/api/sessions/{sessionId}/invites/accept", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var inbox = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync("/api/me/inbox"));
        Assert.Empty(inbox.RootElement.GetProperty("invitations").EnumerateArray());
        Assert.True(await InboxVersionAsync(playerId) > before);
    }

    [SkippableFact]
    public async Task A_declined_invitation_is_absent_from_the_next_inbox_snapshot()
    {
        var (client, playerId) = await Host.CreateSecondPlayerClientAsync();
        var sessionId = await SeedInvitedLobbyAsync(playerId, "now() + interval '1 hour'");
        Assert.Contains(sessionId.ToString(), await client.GetStringAsync("/api/me/inbox"), StringComparison.OrdinalIgnoreCase);
        var before = await InboxVersionAsync(playerId);

        using var response = await client.PostAsync($"/api/sessions/{sessionId}/invites/decline", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var inbox = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync("/api/me/inbox"));
        Assert.Empty(inbox.RootElement.GetProperty("invitations").EnumerateArray());
        Assert.True(await InboxVersionAsync(playerId) > before);
    }

    private PlatformHttpFixture Host => _host ?? throw new InvalidOperationException("The platform host is not configured.");

    private async Task<Guid> SeedInvitedLobbyAsync(Guid inviteeId, string expiresAtSql)
    {
        var sessionId = Guid.NewGuid();
        await Host.ExecuteOwnerAsync("""
            insert into sessions(tenant_id, id, code, format, owner_id, lang, status, capacity, content)
            values (@tenantId, @sessionId, @code, 'async_duel', @ownerId, 'en', 'lobby', 4, '{}'::jsonb)
            """, new { tenantId = Host.TenantId, sessionId, code = $"ACC{sessionId:N}"[..12], ownerId = Host.PlayerId });
        await Host.ExecuteOwnerAsync("""
            insert into session_participants(tenant_id, session_id, player_id, seat, role, status, phase, slot, step)
            values (@tenantId, @sessionId, @playerId, 0, 'player', 'active', 'idle', -1, 0)
            """, new { tenantId = Host.TenantId, sessionId, playerId = Host.PlayerId });
        await Host.ExecuteOwnerAsync($"""
            insert into session_invites(tenant_id, session_id, player_id, invited_by, created_at, expires_at)
            values (@tenantId, @sessionId, @inviteeId, @ownerId, now() - interval '2 hours', {expiresAtSql})
            """, new { tenantId = Host.TenantId, sessionId, inviteeId, ownerId = Host.PlayerId });
        return sessionId;
    }

    private async Task<int> SeatCountAsync(Guid sessionId, Guid playerId) =>
        (await Host.QueryOwnerAsync<int>(
            "select count(*)::int from session_participants where session_id = @sessionId and player_id = @playerId and status = 'active'",
            new { sessionId, playerId })).Single();

    private async Task<int> InviteCountAsync(Guid sessionId, Guid playerId) =>
        (await Host.QueryOwnerAsync<int>(
            "select count(*)::int from session_invites where session_id = @sessionId and player_id = @playerId",
            new { sessionId, playerId })).Single();

    private async Task<long> InboxVersionAsync(Guid playerId) =>
        (await Host.QueryOwnerAsync<long>(
            "select coalesce(max(version), 0)::bigint from player_inbox where player_id = @playerId",
            new { playerId })).Single();
}
