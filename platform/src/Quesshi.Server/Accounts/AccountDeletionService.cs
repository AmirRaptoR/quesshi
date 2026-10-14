using System.Text.Json;
using Quesshi.Infrastructure.Database;

namespace Quesshi.Server.Accounts;

/// <summary>Signals local stream coordinators after the global deletion transaction commits.</summary>
internal interface IAccountDeletionNotifier
{
    void Deleted(IReadOnlyList<AccountDeletionPlayer> players);
}

internal sealed class NullAccountDeletionNotifier : IAccountDeletionNotifier
{
    public void Deleted(IReadOnlyList<AccountDeletionPlayer> players) { }
}

internal sealed record AccountDeletionPlayer(Guid TenantId, Guid PlayerId);

/// <summary>
/// Tombstones an account and durably fans out its tenant cleanup work in one system transaction.
/// The tenant jobs deliberately run later: they are the boundary between global identity removal
/// and player-scoped data scrubbing.
/// </summary>
internal sealed class AccountDeletionService(ISystemDb database, IAccountDeletionNotifier notifier)
{
    public async Task<bool> DeleteAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var players = await database.InTransactionAsync(async transaction =>
        {
            var account = await transaction.QuerySingleOrDefaultAsync<AccountRow>("""
                select email as Email
                from accounts
                where id = @accountId and deleted_at is null
                for update
                """, new { accountId }, cancellationToken);
            if (account is null) return (IReadOnlyList<AccountDeletionPlayer>?)null;

            await transaction.ExecuteAsync("delete from auth_sessions where account_id = @accountId", new { accountId }, cancellationToken);
            await transaction.ExecuteAsync("delete from account_identities where account_id = @accountId", new { accountId }, cancellationToken);
            await transaction.ExecuteAsync("delete from login_codes where email = @email", new { account.Email }, cancellationToken);
            await transaction.ExecuteAsync("""
                update accounts set email = null, deleted_at = now()
                where id = @accountId
                """, new { accountId }, cancellationToken);

            var livePlayers = await transaction.QueryAsync<AccountDeletionPlayer>("""
                select tenant_id as TenantId, player_id as PlayerId
                from account_players(@accountId)
                order by tenant_id, player_id
                """, new { accountId }, cancellationToken);
            foreach (var player in livePlayers)
            {
                var payload = JsonSerializer.Serialize(new AccountPlayerCleanupPayload(player.PlayerId));
                await transaction.ExecuteAsync("""
                    insert into jobs (tenant_id, owner, kind, payload, unique_key, run_at, max_attempts)
                    values (@tenantId, 'core', 'account-player-cleanup', cast(@payload as jsonb), @uniqueKey, now(), null)
                    on conflict do nothing
                    """, new
                {
                    tenantId = player.TenantId,
                    payload,
                    uniqueKey = player.PlayerId.ToString("N")
                }, cancellationToken);

                var streamPayload = $"player:{player.TenantId}:{player.PlayerId}";
                await transaction.ExecuteAsync(
                    "select pg_notify('quesshi_player', @streamPayload)",
                    new { streamPayload },
                    cancellationToken);
            }
            return livePlayers;
        }, cancellationToken);

        if (players is null) return false;
        notifier.Deleted(players);
        return true;
    }

    private sealed record AccountRow(string Email);
}

internal sealed record AccountPlayerCleanupPayload(Guid PlayerId);
