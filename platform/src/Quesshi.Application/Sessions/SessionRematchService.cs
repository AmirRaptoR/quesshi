using System.Text.Json;
using Dapper;
using Quesshi.Engine.Contracts;
using Quesshi.Plugin.Api;

namespace Quesshi.Application.Sessions;

public enum SessionRematchStatus { Created, Existing, NotFound, NotSeated, NotFinished, NotOffered, GuestNotAllowed, Failed }

/// <summary>The lobby a rematch opened (or the one an earlier request already opened) for a finished session.</summary>
public sealed record SessionRematchResult(SessionRematchStatus Status, Guid? SessionId = null, string? Error = null);

/// <summary>Opens the follow-up lobby of a finished session, once, for the session's players.</summary>
public interface ISessionRematch
{
    Task<SessionRematchResult> RematchAsync(ITenantTx transaction, Guid sessionId, Guid playerId, CancellationToken cancellationToken = default);
}

/// <summary>
/// A rematch is a new lobby with the old session's format, content and rules, owned by the player who asked
/// first; every other non-guest player of the old session is invited into it. The unique <c>rematch_of</c>
/// makes it idempotent: the old session row is locked first, so concurrent asks serialize and every later ask
/// (from any of its players) is answered with the same lobby.
/// </summary>
internal sealed class SessionRematchService(ISessions sessions, IFormatRegistry formats, IInbox inbox, TimeProvider time) : ISessionRematch
{
    public async Task<SessionRematchResult> RematchAsync(ITenantTx transaction, Guid sessionId, Guid playerId, CancellationToken cancellationToken = default)
    {
        var old = await transaction.QuerySingleOrDefaultAsync<Original>("""
            select format as Format, status as Status, capacity as Capacity, content::text as Content, rules::text as Rules
            from sessions where id = @sessionId for update
            """, new { sessionId }, cancellationToken);
        if (old is null) return new(SessionRematchStatus.NotFound);

        var seat = await transaction.QuerySingleOrDefaultAsync<bool?>("""
            select true from session_participants where session_id = @sessionId and player_id = @playerId and role = 'player'
            """, new { sessionId, playerId }, cancellationToken);
        if (seat != true) return new(SessionRematchStatus.NotSeated);
        if (old.Status != "finished") return new(SessionRematchStatus.NotFinished);
        if (!formats.TryGet(old.Format, out var format) || format is null || !format.Spec.Rematch) return new(SessionRematchStatus.NotOffered);

        var existing = await transaction.QuerySingleOrDefaultAsync<Guid?>(
            "select id from sessions where rematch_of = @sessionId", new { sessionId }, cancellationToken);
        if (existing is { } known) return new(SessionRematchStatus.Existing, known);

        var asker = await transaction.QuerySingleOrDefaultAsync<bool?>(
            "select account_id is not null from players where id = @playerId", new { playerId }, cancellationToken);
        if (asker != true) return new(SessionRematchStatus.GuestNotAllowed);

        var content = JsonSerializer.Deserialize(old.Content, SessionJsonContext.Default.SessionContent);
        using var rules = JsonDocument.Parse(old.Rules);
        if (content is null) return new(SessionRematchStatus.Failed, Error: "invalid_content");

        var id = Guid.NewGuid();
        var created = await sessions.CreateAsync(transaction, playerId,
            new SessionCreateRequest(id, old.Format, content, rules.RootElement.Clone(), old.Capacity, SessionShape.Lobby), cancellationToken);
        if (created.Status != SessionCreateStatus.Created) return new(SessionRematchStatus.Failed, Error: created.Error ?? "rematch_failed");

        await transaction.ExecuteAsync("update sessions set rematch_of = @sessionId where id = @id", new { sessionId, id }, cancellationToken);
        var invitees = (await transaction.QueryAsync<Guid>("""
            select participant.player_id
            from session_participants participant
            join players on players.id = participant.player_id
            where participant.session_id = @sessionId and participant.role = 'player' and participant.player_id <> @playerId
              and participant.status <> 'left' and players.account_id is not null and players.banned_at is null and players.deleted_at is null
            """, new { sessionId, playerId }, cancellationToken)).ToArray();
        var now = time.GetUtcNow();
        foreach (var invitee in invitees)
        {
            await transaction.ExecuteAsync("""
                insert into session_invites (tenant_id, session_id, player_id, invited_by, created_at, expires_at)
                values (app_tenant(), @id, @invitee, @playerId, @now, @expiresAt)
                on conflict do nothing
                """, new { id, invitee, playerId, now, expiresAt = now.AddDays(365) }, cancellationToken);
            inbox.Changed(transaction, invitee);
        }
        return new(SessionRematchStatus.Created, id);
    }

    private sealed record Original(string Format, string Status, int Capacity, string Content, string Rules);
}
