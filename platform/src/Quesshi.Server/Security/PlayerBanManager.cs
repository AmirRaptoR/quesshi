using Dapper;
using Quesshi.Plugin.Api;

namespace Quesshi.Server.Security;

internal interface IPlayerAccessRevocationNotifier
{
    void RevokePlayer(Guid tenantId, Guid playerId);
}

internal enum PlayerBanChangeStatus { Success, NotFound }

internal sealed record PlayerBanChangeResult(PlayerBanChangeStatus Status);

internal interface IPlayerBanManager
{
    Task<PlayerBanChangeResult> BanAsync(Guid tenantId, Guid playerId, CancellationToken cancellationToken = default);
    Task<PlayerBanChangeResult> UnbanAsync(Guid tenantId, Guid playerId, CancellationToken cancellationToken = default);
}

/// <summary>Changes a tenant player ban while atomically revoking that tenant's player sessions.</summary>
internal sealed class PlayerBanManager(ITenantDb database, ILocks locks, IPlayerAccessRevocationNotifier notifier) : IPlayerBanManager
{
    public Task<PlayerBanChangeResult> BanAsync(Guid tenantId, Guid playerId, CancellationToken cancellationToken = default) =>
        ChangeAsync(tenantId, playerId, ban: true, cancellationToken);

    public Task<PlayerBanChangeResult> UnbanAsync(Guid tenantId, Guid playerId, CancellationToken cancellationToken = default) =>
        ChangeAsync(tenantId, playerId, ban: false, cancellationToken);

    private async Task<PlayerBanChangeResult> ChangeAsync(Guid tenantId, Guid playerId, bool ban, CancellationToken cancellationToken)
    {
        var found = await database.InTenantAsync(tenantId, async transaction =>
        {
            await locks.PlayersAsync(transaction, [playerId], cancellationToken);
            var player = await transaction.QuerySingleOrDefaultAsync<Guid?>("""
                select id from players where id = @playerId and deleted_at is null
                """, new { playerId }, cancellationToken);
            if (player is null) return false;

            await transaction.ExecuteAsync(ban
                ? "update players set banned_at = coalesce(banned_at, now()) where id = @playerId"
                : "update players set banned_at = null where id = @playerId", new { playerId }, cancellationToken);
            if (ban)
            {
                await transaction.ExecuteAsync("select revoke_player_sessions(@playerId)", new { playerId }, cancellationToken);
                await transaction.ExecuteAsync("select pg_notify('quesshi_player', concat('player:', @tenantId, ':', @playerId, ':banned'))", new { tenantId, playerId }, cancellationToken);
            }
            return true;
        }, cancellationToken: cancellationToken);

        if (found && ban) notifier.RevokePlayer(tenantId, playerId);
        return new(found ? PlayerBanChangeStatus.Success : PlayerBanChangeStatus.NotFound);
    }
}
