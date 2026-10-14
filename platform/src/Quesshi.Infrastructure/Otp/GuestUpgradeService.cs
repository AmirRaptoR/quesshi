using Dapper;
using Npgsql;
using Quesshi.Infrastructure.Database;
using Quesshi.Plugin.Api;

namespace Quesshi.Infrastructure.Otp;

internal enum GuestUpgradeState
{
    VerificationFailed,
    AddressTaken,
    GuestUnavailable,
    Upgraded
}

internal sealed record GuestUpgradeResult(GuestUpgradeState State, Guid? AccountId = null);

/// <summary>Verifies an email challenge and claims that account onto an existing guest player.</summary>
internal sealed class GuestUpgradeService(
    EmailAuthService emailAuth,
    DatabaseSources sources,
    ITenantDb tenantDb,
    IEvents events)
{
    public Task<bool> RequestCodeAsync(string email, string clientAddress, string language, CancellationToken cancellationToken = default) =>
        emailAuth.RequestCodeAsync(email, clientAddress, language, cancellationToken);

    public async Task<GuestUpgradeResult> VerifyAndAttachAsync(
        Guid tenantId,
        Guid guestPlayerId,
        string email,
        string code,
        CancellationToken cancellationToken = default)
    {
        var accountId = await emailAuth.VerifyCodeAsync(email, code, cancellationToken);
        return accountId is null
            ? new(GuestUpgradeState.VerificationFailed)
            : await AttachAsync(tenantId, guestPlayerId, accountId.Value, cancellationToken);
    }

    public async Task<GuestUpgradeResult> AttachAsync(
        Guid tenantId,
        Guid guestPlayerId,
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await tenantDb.InTenantAsync(tenantId, async transaction =>
            {
                // claim_account is deliberately the first application command in this transaction:
                // it serializes every attachment for this global account before tenant rows are read.
                await transaction.ExecuteAsync("select claim_account(@accountId)", new { accountId }, cancellationToken);

                var existing = await transaction.QuerySingleOrDefaultAsync<Guid?>("""
                    select id from players where account_id = @accountId
                    """, new { accountId }, cancellationToken);
                if (existing is not null)
                {
                    return new GuestUpgradeResult(GuestUpgradeState.AddressTaken);
                }

                var guest = await transaction.QuerySingleOrDefaultAsync<Guid?>("""
                    select id from players
                    where id = @guestPlayerId and account_id is null and deleted_at is null
                    for update
                    """, new { guestPlayerId }, cancellationToken);
                if (guest is null)
                {
                    return new GuestUpgradeResult(GuestUpgradeState.GuestUnavailable);
                }

                await transaction.ExecuteAsync("""
                    update players set account_id = @accountId
                    where id = @guestPlayerId and account_id is null and deleted_at is null
                    """, new { accountId, guestPlayerId }, cancellationToken);
                await events.EmitAsync(transaction, new PlayerUpgraded(guestPlayerId), cancellationToken);
                return new GuestUpgradeResult(GuestUpgradeState.Upgraded, accountId);
            }, cancellationToken: cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // The unique tenant/account key remains the final guard for callers outside
            // this workflow and for any future change to the account-claim locking path.
            return new(GuestUpgradeState.AddressTaken);
        }
        catch (PostgresException exception) when (exception.SqlState == "P0002")
        {
            return new(GuestUpgradeState.VerificationFailed);
        }
    }

    public async Task RevokeGuestSessionsAsync(
        Guid tenantId,
        Guid guestPlayerId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(sources.SystemConnectionString);
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            delete from auth_sessions where tenant_id = @tenantId and guest_id = @guestPlayerId
            """, new { tenantId, guestPlayerId }, cancellationToken: cancellationToken));
    }
}
