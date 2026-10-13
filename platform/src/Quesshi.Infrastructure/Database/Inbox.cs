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
            "update player_inbox set version = version + 1 where tenant_id = current_setting('app.tenant_id')::uuid and player_id = @playerId",
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
