using Quesshi.Plugin.Api;

namespace Quesshi.Application.Inbox;

/// <summary>Reads the durable, tenant-scoped items a player needs to resume activity.</summary>
public sealed class InboxReader(ITenantDb database, IInboxItems featureItems)
{
    public Task<InboxSnapshot> ReadAsync(
        Guid tenantId,
        Guid playerId,
        bool isGuest,
        CancellationToken cancellationToken = default) =>
        database.InTenantAsync(tenantId, async transaction =>
        {
            var version = await transaction.QuerySingleOrDefaultAsync<long?>("""
                select version
                from player_inbox
                where player_id = @playerId
                """, new { playerId }, cancellationToken);
            IReadOnlyList<InboxInvitation> invitations = isGuest
                ? []
                : await transaction.QueryAsync<InboxInvitation>("""
                    select session_id as SessionId, invited_by as InvitedBy, expires_at as ExpiresAt
                    from session_invites
                    where player_id = @playerId and expires_at > now()
                    order by expires_at, session_id
                    """, new { playerId }, cancellationToken);
            var lobbies = await ReadSessionsAsync(transaction, playerId, "lobby", cancellationToken);
            var turns = await ReadTurnsAsync(transaction, playerId, cancellationToken);
            var items = await featureItems.GetInboxItemsAsync(transaction, playerId, cancellationToken);
            return new InboxSnapshot(version ?? 0, invitations, lobbies, turns, items);
        }, cancellationToken: cancellationToken, readOnly: true);

    private static Task<IReadOnlyList<InboxSession>> ReadSessionsAsync(
        ITenantTx transaction,
        Guid playerId,
        string status,
        CancellationToken cancellationToken) => transaction.QueryAsync<InboxSession>("""
            select sessions.id as Id, sessions.code as Code, sessions.format as Format
            from sessions
            join session_participants on session_participants.session_id = sessions.id
            where sessions.status = @status
              and session_participants.player_id = @playerId
              and session_participants.role = 'player'
              and session_participants.status = 'active'
            order by sessions.created_at, sessions.id
            """, new { playerId, status }, cancellationToken);

    private static Task<IReadOnlyList<InboxSession>> ReadTurnsAsync(
        ITenantTx transaction,
        Guid playerId,
        CancellationToken cancellationToken) => transaction.QueryAsync<InboxSession>("""
            select sessions.id as Id, sessions.code as Code, sessions.format as Format
            from sessions
            join session_participants on session_participants.session_id = sessions.id
            where sessions.status = 'running'
              and session_participants.player_id = @playerId
              and session_participants.role = 'player'
              and session_participants.status = 'active'
              and (
                  sessions.format = 'async_duel'
                  or (
                      sessions.format = 'poll'
                      and not exists (
                          select 1
                          from session_answers
                          where session_answers.session_id = sessions.id
                            and session_answers.player_id = @playerId
                            and session_answers.slot = sessions.slot
                      )
                  )
              )
            order by sessions.created_at, sessions.id
            """, new { playerId }, cancellationToken);
}

/// <summary>The durable inbox response for one authenticated player.</summary>
public sealed record InboxSnapshot(
    long Version,
    IReadOnlyList<InboxInvitation> Invitations,
    IReadOnlyList<InboxSession> Lobbies,
    IReadOnlyList<InboxSession> Turns,
    IReadOnlyList<InboxItem> Items);

/// <summary>An invitation that is still eligible for acceptance.</summary>
public sealed record InboxInvitation(Guid SessionId, Guid InvitedBy, DateTimeOffset ExpiresAt);

/// <summary>A session where the player is seated and can resume participation.</summary>
public sealed record InboxSession(Guid Id, string Code, string Format);
