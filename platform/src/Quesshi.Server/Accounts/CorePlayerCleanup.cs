using System.Text.Json;
using Quesshi.Plugin.Api;

namespace Quesshi.Server.Accounts;

/// <summary>Per-tenant cleanup shared by account deletion and guest retention.</summary>
internal sealed class CorePlayerCleanup(ILocks locks, IPlayerCleanup pluginCleanup)
{
    internal const string DeletedDisplayName = "Deleted player";
    internal const string DeletedAvatarSeed = "deleted";

    public async Task HandleAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = context.Payload.Deserialize<PlayerCleanupPayload>()
            ?? throw new InvalidOperationException("Player cleanup job payload is invalid.");
        var database = context.Database ?? throw new InvalidOperationException("Player cleanup jobs require a tenant.");

        await database.InTenantAsync(context.TenantId!.Value, async transaction =>
        {
            await CleanAsync(transaction, payload.PlayerId, cancellationToken);
            return 0;
        }, cancellationToken: cancellationToken);
    }

    internal async Task<bool> CleanAsync(ITenantTx transaction, Guid playerId, CancellationToken cancellationToken)
    {
        await locks.PlayersAsync(transaction, [playerId], cancellationToken);
        var alreadyDeleted = await transaction.QuerySingleOrDefaultAsync<bool?>(
            "select deleted_at is not null from players where id = @playerId", new { playerId }, cancellationToken);
        if (alreadyDeleted is null) return false;
        await transaction.ExecuteAsync("""
                update players
                set deleted_at = coalesce(deleted_at, now()),
                    display_name = @displayName,
                    avatar_seed = @avatarSeed
                where id = @playerId
                """, new
            {
                playerId,
                displayName = DeletedDisplayName,
                avatarSeed = DeletedAvatarSeed
            }, cancellationToken);

        // Packs and player-written questions go with the player; sessions already played keep
        // their own item snapshots, and only the link to the question is cleared.
        var owned = (await transaction.QueryAsync<Guid>(
            "select id from packs where owner_id = @playerId union select id from questions where owner_id = @playerId",
            new { playerId }, cancellationToken)).ToArray();
        await locks.ContentAsync(transaction, owned, cancellationToken);
        await transaction.ExecuteAsync("delete from packs where owner_id = @playerId", new { playerId }, cancellationToken);
        await transaction.ExecuteAsync("delete from questions where owner_id = @playerId", new { playerId }, cancellationToken);

        // Enqueuing occurs in the same tenant transaction as the scrub, so plugin handlers
        // cannot observe a live player and do not become runnable before this commit.
        // A worker crash after this transaction commits can replay the core job; avoid
        // re-running installed plugin cleanup after it has already completed.
        if (alreadyDeleted is false) await pluginCleanup.EnqueueAsync(transaction, playerId, cancellationToken);
        return alreadyDeleted is false;
    }
}
