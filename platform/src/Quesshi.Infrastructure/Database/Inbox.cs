using Quesshi.Plugin.Api;

namespace Quesshi.Infrastructure.Database;

/// <summary>Applies an inbox bump inside the transaction which collected it.</summary>
public interface IInboxBumpSink
{
    Task ApplyAsync(ITenantTx transaction, Guid playerId, CancellationToken cancellationToken = default);
}

public sealed class PlayerInboxBumpSink : IInboxBumpSink
{
    public Task ApplyAsync(ITenantTx transaction, Guid playerId, CancellationToken cancellationToken = default) =>
        transaction.ExecuteAsync(
            "with bumped as (" +
            "  insert into player_inbox (tenant_id, player_id, version) " +
            "  values (current_setting('app.tenant_id')::uuid, @playerId, 1) " +
            "  on conflict (tenant_id, player_id) do update set version = player_inbox.version + 1 " +
            "  returning version" +
            ") " +
            "select pg_notify('quesshi_inbox', format('inbox:%s:%s:%s', current_setting('app.tenant_id'), @playerId, version)) from bumped",
            new { playerId },
            cancellationToken);
}

public sealed class TenantInbox : IInbox
{
    public void Changed(ITenantTx transaction, Guid playerId)
    {
        if (transaction is not TenantTransaction tenantTransaction)
        {
            throw new ArgumentException("The transaction was not created by ITenantDb.", nameof(transaction));
        }

        tenantTransaction.RecordInboxChange(playerId);
    }
}
