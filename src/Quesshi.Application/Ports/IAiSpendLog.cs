namespace Quesshi.Application.Ports;

/// <summary>
/// Records what each model call cost, so the admin panel can answer "how much have I spent".
/// One row per call: a few a day, and the totals are a single grouped query.
/// </summary>
public interface IAiSpendLog
{
    /// <summary>Must not throw — a bookkeeping failure is not worth losing generated questions over.</summary>
    Task RecordAsync(AiCall call, CancellationToken ct = default);

    /// <summary>Totals over calls made at or after <paramref name="since"/>; everything when null.</summary>
    Task<AiSpend> TotalsAsync(DateTimeOffset? since = null, CancellationToken ct = default);
}
