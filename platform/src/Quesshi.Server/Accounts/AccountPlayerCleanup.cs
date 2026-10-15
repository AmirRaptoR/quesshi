using System.Text.Json;
using Quesshi.Plugin.Api;

namespace Quesshi.Server.Accounts;

/// <summary>Per-tenant cleanup handler run after an account has been globally tombstoned.</summary>
internal sealed class AccountPlayerCleanup(ILocks locks, IPlayerCleanup pluginCleanup)
{
    internal const string DeletedDisplayName = "Deleted player";
    internal const string DeletedAvatarSeed = "deleted";

    public async Task HandleAsync(JobContext context, CancellationToken cancellationToken)
    {
        var payload = context.Payload.Deserialize<AccountPlayerCleanupPayload>()
            ?? throw new InvalidOperationException("Account player cleanup job payload is invalid.");
        var database = context.Database ?? throw new InvalidOperationException("Account player cleanup jobs require a tenant.");

        await database.InTenantAsync(context.TenantId!.Value, async transaction =>
        {
            await locks.PlayersAsync(transaction, [payload.PlayerId], cancellationToken);
            var alreadyDeleted = await transaction.QuerySingleOrDefaultAsync<bool?>(
                "select deleted_at is not null from players where id = @playerId", new { playerId = payload.PlayerId }, cancellationToken);
            await transaction.ExecuteAsync("""
                update players
                set deleted_at = coalesce(deleted_at, now()),
                    display_name = @displayName,
                    avatar_seed = @avatarSeed
                where id = @playerId
                """, new
            {
                playerId = payload.PlayerId,
                displayName = DeletedDisplayName,
                avatarSeed = DeletedAvatarSeed
            }, cancellationToken);

            // Enqueuing occurs in the same tenant transaction as the scrub, so plugin handlers
            // cannot observe a live player and do not become runnable before this commit.
            // A worker crash after this transaction commits can replay the core job; avoid
            // re-running installed plugin cleanup after it has already completed.
            if (alreadyDeleted is false) await pluginCleanup.EnqueueAsync(transaction, payload.PlayerId, cancellationToken);
            return 0;
        }, cancellationToken: cancellationToken);
    }
}
