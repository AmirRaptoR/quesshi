using System.Data;
using System.Globalization;
using Quesshi.Plugin.Api;

namespace Quesshi.Features.AiGeneration;

/// <summary>A model request whose upper spend bound is known before it is sent.</summary>
public sealed record AiProviderRequest(
    Guid CallId,
    Guid? RunId,
    string Purpose,
    string Model,
    int InputTokens,
    int MaxOutputTokens,
    decimal InputUsdPerToken,
    decimal OutputUsdPerToken)
{
    public decimal UpperBoundUsd => RoundUpToMicrousd(
        (InputTokens * InputUsdPerToken) + (MaxOutputTokens * OutputUsdPerToken));

    private static decimal RoundUpToMicrousd(decimal value) => decimal.Ceiling(value * 1_000_000m) / 1_000_000m;
}

public sealed record AiUsage(int InputTokens, int OutputTokens, decimal CostUsd);

public enum AiReserveResult
{
    Reserved,
    BudgetExceeded,
    AlreadyRecorded
}

public sealed record AiRecoveryResult(int Expired, int Unknown);

/// <summary>
/// Persists the spend safety protocol around an external model call. The caller must send only
/// after <see cref="TrySendAsync"/> returns true; no database transaction reaches the provider.
/// </summary>
public sealed class BudgetLedger(ITenantDb database, TimeProvider timeProvider)
{
    private static readonly TimeSpan ReservationLifetime = TimeSpan.FromMinutes(15);

    public Task<AiReserveResult> ReserveAsync(Guid tenantId, AiProviderRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var now = timeProvider.GetUtcNow();
        var month = now.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var bound = request.UpperBoundUsd;

        return database.InTenantAsync(tenantId, async transaction =>
        {
            var existing = await transaction.QuerySingleOrDefaultAsync<string>("""
                select status
                from ai_calls
                where id = @callId
                for update
                """, new { callId = request.CallId }, cancellationToken);
            if (existing is not null) return AiReserveResult.AlreadyRecorded;

            var reserved = await transaction.QuerySingleOrDefaultAsync<string>("""
                update ai_budgets
                set reserved_usd = reserved_usd + @bound
                where month = @month
                  and spent_usd + reserved_usd + @bound <= limit_usd
                returning month
                """, new { month, bound }, cancellationToken);
            if (reserved is null) return AiReserveResult.BudgetExceeded;

            await transaction.ExecuteAsync("""
                insert into ai_calls(
                    tenant_id, id, run_id, month, purpose, model, status, reserved_usd, input_tokens, output_tokens, at)
                values (@tenantId, @callId, @runId, @month, @purpose, @model, 'reserved', @bound, @inputTokens, @maxOutputTokens, @now)
                """, new
            {
                tenantId,
                callId = request.CallId,
                request.RunId,
                month,
                request.Purpose,
                request.Model,
                bound,
                request.InputTokens,
                request.MaxOutputTokens,
                now
            }, cancellationToken);
            return AiReserveResult.Reserved;
        }, IsolationLevel.Serializable, cancellationToken);
    }

    /// <summary>Atomically grants one worker the right to send a previously reserved request.</summary>
    public async Task<bool> TrySendAsync(Guid tenantId, Guid callId, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        return await database.InTenantAsync(tenantId, transaction => transaction.QuerySingleOrDefaultAsync<Guid?>("""
            update ai_calls
            set status = 'sent', at = @now
            where id = @callId and status = 'reserved'
            returning id
            """, new { callId, now }, cancellationToken), cancellationToken: cancellationToken) is not null;
    }

    /// <summary>Records the final provider usage once, releasing either the held or estimated bound.</summary>
    public Task<bool> SettleAsync(Guid tenantId, Guid callId, AiUsage usage, CancellationToken cancellationToken = default)
    {
        ValidateUsage(usage);
        return database.InTenantAsync(tenantId, async transaction =>
        {
            var call = await LoadCallAsync(transaction, callId, cancellationToken);
            if (call is null || (call.Status != "sent" && call.Status != "unknown")) return false;

            var budgetChanged = call.Status == "sent"
                ? await transaction.ExecuteAsync("""
                    update ai_budgets
                    set reserved_usd = reserved_usd - @bound,
                        spent_usd = spent_usd + @costUsd
                    where month = @month and reserved_usd >= @bound
                    """, new { call.Month, bound = call.ReservedUsd, costUsd = usage.CostUsd }, cancellationToken)
                : await transaction.ExecuteAsync("""
                    update ai_budgets
                    set spent_usd = spent_usd - @bound + @costUsd
                    where month = @month and spent_usd >= @bound
                    """, new { call.Month, bound = call.ReservedUsd, costUsd = usage.CostUsd }, cancellationToken);
            if (budgetChanged != 1) throw new InvalidOperationException("The AI budget no longer matches the call ledger.");

            await transaction.ExecuteAsync("""
                update ai_calls
                set status = 'done', reserved_usd = 0, input_tokens = @inputTokens,
                    output_tokens = @outputTokens, cost_usd = @costUsd, at = @now
                where id = @callId
                """, new
            {
                callId,
                usage.InputTokens,
                usage.OutputTokens,
                usage.CostUsd,
                now = timeProvider.GetUtcNow()
            }, cancellationToken);
            return true;
        }, cancellationToken: cancellationToken);
    }

    /// <summary>Expires unsent calls and marks overdue sent calls unknown without sending them again.</summary>
    public Task<AiRecoveryResult> RecoverAsync(
        Guid tenantId,
        TimeSpan providerTimeout,
        TimeSpan timeoutMargin,
        CancellationToken cancellationToken = default)
    {
        if (providerTimeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(providerTimeout));
        if (timeoutMargin < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeoutMargin));
        var now = timeProvider.GetUtcNow();
        var reservedBefore = now - ReservationLifetime;
        var sentBefore = now - providerTimeout - timeoutMargin;

        return database.InTenantAsync(tenantId, async transaction =>
        {
            var calls = await transaction.QueryAsync<CallRow>("""
                select status as Status, month as Month, reserved_usd as ReservedUsd, id as Id
                from ai_calls
                where (status = 'reserved' and at <= @reservedBefore)
                   or (status = 'sent' and at <= @sentBefore)
                order by month, id
                for update
                """, new { reservedBefore, sentBefore }, cancellationToken);
            var expired = 0;
            var unknown = 0;
            foreach (var call in calls)
            {
                if (call.Status == "reserved")
                {
                    await ReleaseReservationAsync(transaction, call, cancellationToken);
                    await transaction.ExecuteAsync("""
                        update ai_calls set status = 'expired', reserved_usd = 0, at = @now where id = @id
                        """, new { call.Id, now }, cancellationToken);
                    expired++;
                }
                else
                {
                    var changed = await transaction.ExecuteAsync("""
                        update ai_budgets
                        set reserved_usd = reserved_usd - @bound, spent_usd = spent_usd + @bound
                        where month = @month and reserved_usd >= @bound
                        """, new { call.Month, bound = call.ReservedUsd }, cancellationToken);
                    if (changed != 1) throw new InvalidOperationException("The AI budget no longer matches the call ledger.");
                    await transaction.ExecuteAsync("update ai_calls set status = 'unknown', at = @now where id = @id", new { call.Id, now }, cancellationToken);
                    unknown++;
                }
            }
            return new AiRecoveryResult(expired, unknown);
        }, cancellationToken: cancellationToken);
    }

    private static Task<CallRow?> LoadCallAsync(ITenantTx transaction, Guid callId, CancellationToken cancellationToken) =>
        transaction.QuerySingleOrDefaultAsync<CallRow>("""
            select status as Status, month as Month, reserved_usd as ReservedUsd, id as Id
            from ai_calls where id = @callId for update
            """, new { callId }, cancellationToken);

    private static async Task ReleaseReservationAsync(ITenantTx transaction, CallRow call, CancellationToken cancellationToken)
    {
        var changed = await transaction.ExecuteAsync("""
            update ai_budgets set reserved_usd = reserved_usd - @bound
            where month = @month and reserved_usd >= @bound
            """, new { call.Month, bound = call.ReservedUsd }, cancellationToken);
        if (changed != 1) throw new InvalidOperationException("The AI budget no longer matches the call ledger.");
    }

    private static void ValidateRequest(AiProviderRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        if (request.InputTokens < 0) throw new ArgumentOutOfRangeException(nameof(request.InputTokens));
        if (request.MaxOutputTokens < 0) throw new ArgumentOutOfRangeException(nameof(request.MaxOutputTokens));
        if (request.InputUsdPerToken < 0) throw new ArgumentOutOfRangeException(nameof(request.InputUsdPerToken));
        if (request.OutputUsdPerToken < 0) throw new ArgumentOutOfRangeException(nameof(request.OutputUsdPerToken));
        if (request.UpperBoundUsd > 999_999.999999m) throw new ArgumentOutOfRangeException(nameof(request));
    }

    private static void ValidateUsage(AiUsage usage)
    {
        if (usage.InputTokens < 0) throw new ArgumentOutOfRangeException(nameof(usage.InputTokens));
        if (usage.OutputTokens < 0) throw new ArgumentOutOfRangeException(nameof(usage.OutputTokens));
        if (usage.CostUsd < 0 || usage.CostUsd > 999_999.999999m) throw new ArgumentOutOfRangeException(nameof(usage.CostUsd));
    }

    private sealed record CallRow(string Status, string Month, decimal ReservedUsd, Guid Id);
}
