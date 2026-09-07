namespace Quesshi.Application.Ports;

/// <summary>Durable index of every match, written when it starts and again when it ends.</summary>
public interface IMatchArchive
{
    Task SaveAsync(ArchivedMatch match, CancellationToken ct = default);
    Task<ArchivedMatch?> ByCodeAsync(string code, CancellationToken ct = default);
    Task<IReadOnlyList<ArchivedMatch>> ForPlayerAsync(string playerId, int take, CancellationToken ct = default);
    Task<long> CountAsync(CancellationToken ct = default);

    /// <summary>Rows with <see cref="ArchivedMatch.IsLive"/> set — the dashboard's live-duels total.
    /// <see cref="CountAsync"/> keeps counting every row, live and async alike, as it always has.</summary>
    Task<long> CountLiveAsync(CancellationToken ct = default);
}
