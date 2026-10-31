using Quesshi.Plugin.Api;

namespace Quesshi.Application.Sessions;

public enum SessionInviteStatus
{
    Invited,
    SessionNotFound,
    NotSeated,
    InviteeUnavailable,
    NotEligible,
    AlreadySeated,
    SessionClosed,
}

/// <summary>Creates durable invitations for players who are eligible to receive them.</summary>
public interface ISessionInvitations
{
    Task<SessionInviteStatus> InviteAsync(
        ITenantTx transaction,
        Guid sessionId,
        Guid inviterId,
        Guid inviteeId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns invitation persistence, expiry and guest restrictions. An invitee must be a recent co-player of the
/// inviter or, while the friends feature is enabled, one of the inviter's friends.
/// </summary>
internal sealed class SessionInvitationService(INamedQueries queries, IInbox inbox, TimeProvider time) : ISessionInvitations
{
    private static readonly TimeSpan RecentCoPlayerWindow = TimeSpan.FromDays(30);
    private static readonly TimeSpan PollInvitationLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LobbyInvitationLifetime = TimeSpan.FromHours(24);

    public async Task<SessionInviteStatus> InviteAsync(
        ITenantTx transaction,
        Guid sessionId,
        Guid inviterId,
        Guid inviteeId,
        CancellationToken cancellationToken = default)
    {
        var session = await transaction.QuerySingleOrDefaultAsync<SessionRow>(
            "select format as Format, status as Status from sessions where id = @sessionId",
            new { sessionId },
            cancellationToken);
        if (session is null) return SessionInviteStatus.SessionNotFound;

        var seated = await transaction.QuerySingleOrDefaultAsync<bool?>("""
            select true from session_participants
            where session_id = @sessionId and player_id = @inviterId and role = 'player' and status = 'active'
            """, new { sessionId, inviterId }, cancellationToken);
        if (seated != true) return SessionInviteStatus.NotSeated;
        if (session.Status is not ("lobby" or "running")) return SessionInviteStatus.SessionClosed;

        // Guests, banned and deleted players, and the inviter themself can never be invited.
        var inviteeAvailable = inviteeId != inviterId && await transaction.QuerySingleOrDefaultAsync<bool?>("""
            select true from players
            where id = @inviteeId and account_id is not null and banned_at is null and deleted_at is null
            """, new { inviteeId }, cancellationToken) == true;
        if (!inviteeAvailable) return SessionInviteStatus.InviteeUnavailable;

        var alreadySeated = await transaction.QuerySingleOrDefaultAsync<bool?>(
            "select true from session_participants where session_id = @sessionId and player_id = @inviteeId and status = 'active'",
            new { sessionId, inviteeId },
            cancellationToken);
        if (alreadySeated == true) return SessionInviteStatus.AlreadySeated;

        if (!await IsEligibleAsync(transaction, inviterId, inviteeId, cancellationToken)) return SessionInviteStatus.NotEligible;

        var now = time.GetUtcNow();
        var lifetime = session.Format == "poll" ? PollInvitationLifetime : LobbyInvitationLifetime;
        await transaction.ExecuteAsync("""
            insert into session_invites (tenant_id, session_id, player_id, invited_by, created_at, expires_at)
            values (current_setting('app.tenant_id')::uuid, @sessionId, @inviteeId, @inviterId, @now, @expiresAt)
            on conflict (tenant_id, session_id, player_id) do update
              set invited_by = excluded.invited_by, created_at = excluded.created_at, expires_at = excluded.expires_at
            """, new { sessionId, inviteeId, inviterId, now, expiresAt = now + lifetime }, cancellationToken);
        inbox.Changed(transaction, inviteeId);
        return SessionInviteStatus.Invited;
    }

    private async Task<bool> IsEligibleAsync(ITenantTx transaction, Guid inviterId, Guid inviteeId, CancellationToken cancellationToken)
    {
        var recent = await transaction.QuerySingleOrDefaultAsync<bool?>("""
            select true
            from session_participants mine
            join session_participants theirs on theirs.session_id = mine.session_id and theirs.player_id = @inviteeId
            join sessions on sessions.id = mine.session_id
            where mine.player_id = @inviterId
              and sessions.created_at >= @since
            limit 1
            """, new { inviterId, inviteeId, since = time.GetUtcNow() - RecentCoPlayerWindow }, cancellationToken);
        if (recent == true) return true;

        var friends = await queries.InvokeAsync<IReadOnlyList<Guid>>(
            NamedQueryConsumers.Core, "friends.ids", transaction, inviterId, cancellationToken);
        return friends is { Available: true, Value: { } ids } && ids.Contains(inviteeId);
    }

    private sealed record SessionRow(string Format, string Status);
}
