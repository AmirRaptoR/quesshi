namespace Quesshi.Application.Ports;

public interface ILeaderboard
{
    /// <summary>
    /// Sets a player's score to <paramref name="total"/> absolutely, rather than incrementing or
    /// decrementing it. The leaderboard is a projection of <c>Player.Stats.TotalScore</c>, which is
    /// already authoritative and already floored at zero, so there is nothing left for this store to
    /// compute — it just mirrors the number it is handed. That is what makes it safe to call more than
    /// once for the same settlement: a retry that repeats the identical total is a no-op by
    /// construction, not a second increment or a second penalty racing the first past a floor.
    /// </summary>
    Task SetAsync(string playerId, long total, CancellationToken ct = default);
    Task<IReadOnlyList<LeaderboardEntry>> TopAsync(int count, CancellationToken ct = default);
    Task<IReadOnlyList<LeaderboardEntry>> AmongAsync(IReadOnlyCollection<string> playerIds, CancellationToken ct = default);
}
