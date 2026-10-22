using System.Net.Http.Json;
using System.Text.Json;
using Quesshi.Plugin.Api;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

public sealed class InboxEndpointDatabaseTests : IAsyncLifetime
{
    private static readonly bool IsPostgresConfigured = PostgresFixture.IsConfigured(
        Environment.GetEnvironmentVariable(PostgresFixture.AdminConnectionStringEnvironmentVariable),
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase));
    private PlatformHttpFixture? _host;

    public async Task InitializeAsync()
    {
        if (!IsPostgresConfigured) return;
        _host = new PlatformHttpFixture([new InboxHttpFeature()]);
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task Inbox_returns_the_persisted_version()
    {
        await Host.ExecuteOwnerAsync("""
            insert into player_inbox(tenant_id, player_id, version)
            values (@tenantId, @playerId, 7)
            """, new { tenantId = Host.TenantId, playerId = Host.PlayerId });

        var inbox = await GetInboxAsync(Host.Player);

        Assert.Equal(7, inbox.Version);
    }

    [SkippableFact]
    public async Task Inbox_defaults_the_version_to_zero_without_a_row()
    {
        var inbox = await GetInboxAsync(Host.Player);

        Assert.Equal(0, inbox.Version);
    }

    [SkippableFact]
    public async Task Inbox_returns_an_unexpired_invitation_addressed_to_the_player()
    {
        var sessionId = await SeedInvitationAsync("INBOXINVITE", "now() + interval '1 hour'");

        var inbox = await GetInboxAsync(Host.Player);

        Assert.Contains(inbox.Invitations, invitation => invitation.SessionId == sessionId);
    }

    [SkippableFact]
    public async Task Inbox_omits_an_expired_invitation_addressed_to_the_player()
    {
        var sessionId = await SeedInvitationAsync("INBOXEXPIRED", "now() - interval '1 hour'");

        var inbox = await GetInboxAsync(Host.Player);

        Assert.DoesNotContain(inbox.Invitations, invitation => invitation.SessionId == sessionId);
    }

    [SkippableFact]
    public async Task Inbox_returns_a_lobby_where_the_player_is_seated()
    {
        var sessionId = Guid.NewGuid();
        await SeedSessionAsync(sessionId, "INBOXLOBBY", "lobby");
        await SeedActiveParticipantAsync(sessionId);

        var inbox = await GetInboxAsync(Host.Player);

        Assert.Contains(inbox.Lobbies, session => session.Id == sessionId);
    }

    [SkippableFact]
    public async Task Inbox_returns_a_running_session_when_the_players_turn_is_due()
    {
        var sessionId = Guid.NewGuid();
        await SeedSessionAsync(sessionId, "INBOXTURN", "running");
        await SeedActiveParticipantAsync(sessionId);

        var inbox = await GetInboxAsync(Host.Player);

        Assert.Contains(inbox.Turns, session => session.Id == sessionId);
    }

    [SkippableFact]
    public async Task Inbox_omits_a_poll_after_the_player_submits_for_its_current_slot()
    {
        var sessionId = Guid.NewGuid();
        await SeedSessionAsync(sessionId, "INBOXPOLL", "running", format: "poll", slot: 0);
        await SeedActiveParticipantAsync(sessionId);
        await Host.ExecuteOwnerAsync("""
            insert into session_items(tenant_id, session_id, slot, kind, kind_version, prompt, payload, resolved, card, reveal)
            values (@tenantId, @sessionId, 0, 'poll_choice', 1, 'Question', '{}'::jsonb, '{}'::jsonb, '{}'::jsonb, '{}'::jsonb);
            insert into session_answers(tenant_id, session_id, slot, player_id, answer, points, received_at)
            values (@tenantId, @sessionId, 0, @playerId, '{}'::jsonb, 0, now());
            """, new { tenantId = Host.TenantId, sessionId, playerId = Host.PlayerId });

        var inbox = await GetInboxAsync(Host.Player);

        Assert.DoesNotContain(inbox.Turns, session => session.Id == sessionId);
    }

    [SkippableFact]
    public async Task Inbox_returns_items_from_enabled_feature_handlers()
    {
        var inbox = await GetInboxAsync(Host.Player);

        Assert.Contains(inbox.Items, item => item.Type == "inbox_http");
    }

    [SkippableFact]
    public async Task Inbox_does_not_expose_another_tenants_plausible_invitation()
    {
        var otherTenant = Guid.NewGuid();
        var otherPlayer = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await Host.ExecuteOwnerAsync("""
            insert into tenants(id, slug, name, default_language)
            values (@otherTenant, @otherSlug, 'Other inbox tenant', 'en');
            insert into players(tenant_id, id, account_id, display_name, avatar_seed, lang)
            values
                (@otherTenant, @otherPlayer, null, 'Other inbox owner', 'other-owner', 'en'),
                (@otherTenant, @playerId, null, 'Other inbox target', 'other-target', 'en');
            insert into sessions(tenant_id, id, code, format, owner_id, lang, status, capacity, content)
            values (@otherTenant, @sessionId, 'OTHERINBOX', 'async_duel', @otherPlayer, 'en', 'lobby', 2, '{}'::jsonb);
            insert into session_invites(tenant_id, session_id, player_id, invited_by, expires_at)
            values (@otherTenant, @sessionId, @playerId, @otherPlayer, now() + interval '1 hour');
            """, new
        {
            otherTenant,
            otherSlug = $"other-inbox-{otherTenant:N}",
            otherPlayer,
            playerId = Host.PlayerId,
            sessionId,
        });

        var inbox = await GetInboxAsync(Host.Player);

        Assert.DoesNotContain(inbox.Invitations, invitation => invitation.SessionId == sessionId);
    }

    [SkippableFact]
    public async Task Inbox_does_not_return_invitations_to_a_guest()
    {
        var (guest, guestId) = await Host.CreateAnotherGuestClientAsync();
        var inviter = (await Host.CreateSecondPlayerClientAsync()).PlayerId;
        var sessionId = Guid.NewGuid();
        await SeedSessionAsync(sessionId, "INBOXGUEST", "lobby", inviter);
        await Host.ExecuteOwnerAsync("""
            insert into session_invites(tenant_id, session_id, player_id, invited_by, expires_at)
            values (@tenantId, @sessionId, @guestId, @inviter, now() + interval '1 hour')
            """, new { tenantId = Host.TenantId, sessionId, guestId, inviter });

        var inbox = await GetInboxAsync(guest);

        Assert.DoesNotContain(inbox.Invitations, invitation => invitation.SessionId == sessionId);
    }

    private PlatformHttpFixture Host
    {
        get
        {
            Skip.If(!IsPostgresConfigured, "PostgreSQL is not configured.");
            return _host ?? throw new InvalidOperationException("The platform HTTP host did not start.");
        }
    }

    private static async Task<InboxResponse> GetInboxAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/me/inbox");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<InboxResponse>()
            ?? throw new InvalidOperationException("The inbox response was empty.");
    }

    private async Task<Guid> SeedInvitationAsync(string code, string expiresAt)
    {
        var inviter = (await Host.CreateSecondPlayerClientAsync()).PlayerId;
        var sessionId = Guid.NewGuid();
        await SeedSessionAsync(sessionId, code, "lobby", inviter);
        await Host.ExecuteOwnerAsync($"""
            insert into session_invites(tenant_id, session_id, player_id, invited_by, expires_at)
            values (@tenantId, @sessionId, @playerId, @inviter, {expiresAt})
            """, new { tenantId = Host.TenantId, sessionId, playerId = Host.PlayerId, inviter });
        return sessionId;
    }

    private async Task SeedSessionAsync(Guid sessionId, string code, string status, Guid? ownerId = null, string format = "async_duel", int slot = -1)
    {
        await Host.ExecuteOwnerAsync("""
            insert into sessions(tenant_id, id, code, format, owner_id, lang, status, capacity, content, slot, started_at)
            values (@tenantId, @sessionId, @code, @format, @ownerId, 'en', @status, 2, '{}'::jsonb, @slot,
                case when @status = 'running' then now() else null end)
            """, new { tenantId = Host.TenantId, sessionId, code, status, format, slot, ownerId = ownerId ?? Host.PlayerId });
    }

    private Task SeedActiveParticipantAsync(Guid sessionId) => Host.ExecuteOwnerAsync("""
        insert into session_participants(tenant_id, session_id, player_id, seat, role, status, phase, slot, step)
        values (@tenantId, @sessionId, @playerId, 0, 'player', 'active', 'idle', -1, 0)
        """, new { tenantId = Host.TenantId, sessionId, playerId = Host.PlayerId });

    private sealed record InboxResponse(
        long Version,
        IReadOnlyList<InboxInvitation> Invitations,
        IReadOnlyList<InboxSession> Lobbies,
        IReadOnlyList<InboxSession> Turns,
        IReadOnlyList<InboxItem> Items);

    private sealed record InboxInvitation(Guid SessionId);

    private sealed record InboxSession(Guid Id);

    private sealed class InboxHttpFeature : IFeature
    {
        public string Id => "inbox_http";
        public FeatureSettings Settings => FeatureSettings.Create(new InboxHttpSettings(), _ => []);

        public void Configure(IFeatureBuilder feature) => feature.Inbox(
            static (_, _, _) => Task.FromResult<IReadOnlyList<InboxItem>>(
                [new InboxItem("inbox_http", JsonSerializer.SerializeToElement(new { source = "fixture" }))]));
    }

    private sealed record InboxHttpSettings;
}
