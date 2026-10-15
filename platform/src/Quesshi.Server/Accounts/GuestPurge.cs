using System.Text.Json;
using Quesshi.Engine.Contracts;
using Quesshi.Infrastructure.Database;
using Quesshi.Plugin.Api;

namespace Quesshi.Server.Accounts;

/// <summary>Daily tenant job that retires abandoned, unauthenticated guest players.</summary>
internal sealed class GuestPurge(ILocks locks, CorePlayerCleanup cleanup, IJobs jobs, ISystemDb system, IClock clock)
{
    public async Task HandleAsync(JobContext context, CancellationToken cancellationToken)
    {
        var database = context.Database ?? throw new InvalidOperationException("Guest purge jobs require a tenant.");
        var tenantId = context.TenantId ?? throw new InvalidOperationException("Guest purge jobs require a tenant.");
        var cutoff = clock.Now - TimeSpan.FromDays(30);
        var candidates = await database.InTenantAsync(tenantId, transaction => transaction.QueryAsync<Guid>("""
            select id from players
            where account_id is null and deleted_at is null
              and created_at < @cutoff
            order by id
            """, new { cutoff }, cancellationToken), cancellationToken: cancellationToken);

        foreach (var playerId in candidates)
        {
            await database.InTenantAsync(tenantId, async transaction =>
            {
                // FOR UPDATE makes a concurrent auth-session or participant foreign-key insert
                // settle before the final eligibility check.
                await locks.PlayersAsync(transaction, [playerId], cancellationToken);
                var liveGuest = await transaction.QuerySingleOrDefaultAsync<Guid?>("""
                    select id from players
                    where id = @playerId and account_id is null and deleted_at is null
                      and created_at < @cutoff
                    for update
                    """, new { playerId, cutoff }, cancellationToken);
                if (liveGuest is null) return 0;

                var hasAuthSession = await system.QuerySingleOrDefaultAsync<bool>("""
                    select exists(
                        select 1 from auth_sessions
                        where tenant_id = @tenantId and guest_id = @playerId)
                    """, new { tenantId, playerId }, cancellationToken);
                if (hasAuthSession) return 0;

                var hasUnfinishedSeat = await transaction.QuerySingleOrDefaultAsync<bool>("""
                    select exists(
                        select 1
                        from session_participants participant
                        join sessions session on session.tenant_id = participant.tenant_id and session.id = participant.session_id
                        where participant.player_id = @playerId
                          and session.status not in ('finished', 'cancelled'))
                    """, new { playerId }, cancellationToken);
                if (hasUnfinishedSeat) return 0;

                var hasHistory = await transaction.QuerySingleOrDefaultAsync<bool>("""
                    select exists(select 1 from session_participants where player_id = @playerId)
                    """, new { playerId }, cancellationToken);
                if (!await cleanup.CleanAsync(transaction, playerId, cancellationToken)) return 0;

                if (!hasHistory)
                {
                    await jobs.EnqueueAsync(transaction, "core", "guest-remove-player",
                        JsonSerializer.SerializeToElement(new PlayerCleanupPayload(playerId)),
                        $"guest-remove:{playerId:N}", maxAttempts: null, cancellationToken: cancellationToken);
                }
                return 0;
            }, cancellationToken: cancellationToken);
        }
    }
}

/// <summary>Deletes a purged guest only after every installed cleanup handler has observed its tombstone.</summary>
internal sealed class GuestPlayerRemoval(ILocks locks, ISystemDb system)
{
    public async Task HandleAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = context.Payload.Deserialize<PlayerCleanupPayload>()
            ?? throw new InvalidOperationException("Guest player removal job payload is invalid.");
        var database = context.Database ?? throw new InvalidOperationException("Guest player removal jobs require a tenant.");
        var tenantId = context.TenantId ?? throw new InvalidOperationException("Guest player removal jobs require a tenant.");

        var cleanupStillRunning = await system.QuerySingleOrDefaultAsync<bool>("""
            select exists(
                select 1 from jobs
                where tenant_id = @tenantId and owner = 'core' and kind = 'cleanup-player'
                  and coalesce(payload->>'playerId', payload->>'PlayerId') = @playerIdText and status <> 'done')
            """, new { tenantId, playerIdText = payload.PlayerId.ToString() }, cancellationToken);
        if (cleanupStillRunning) throw new InvalidOperationException("Installed player cleanup is still running.");

        await database.InTenantAsync(tenantId, async transaction =>
        {
            await locks.PlayersAsync(transaction, [payload.PlayerId], cancellationToken);
            var deletedGuest = await transaction.QuerySingleOrDefaultAsync<Guid?>("""
                select id from players
                where id = @playerId and account_id is null and deleted_at is not null
                for update
                """, new { playerId = payload.PlayerId }, cancellationToken);
            if (deletedGuest is null) return 0;
            var hasSeats = await transaction.QuerySingleOrDefaultAsync<bool>(
                "select exists(select 1 from session_participants where player_id = @playerId)",
                new { playerId = payload.PlayerId }, cancellationToken);
            var hasAuthSession = await system.QuerySingleOrDefaultAsync<bool>("""
                select exists(select 1 from auth_sessions where tenant_id = @tenantId and guest_id = @playerId)
                """, new { tenantId, playerId = payload.PlayerId }, cancellationToken);
            if (!hasSeats && !hasAuthSession)
            {
                await transaction.ExecuteAsync("delete from players where id = @playerId", new { playerId = payload.PlayerId }, cancellationToken);
            }
            return 0;
        }, cancellationToken: cancellationToken);
    }
}
