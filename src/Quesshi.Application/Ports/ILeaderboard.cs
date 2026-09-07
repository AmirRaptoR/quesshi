namespace Quesshi.Application.Ports;

public interface ILeaderboard
{
    Task AddAsync(string playerId, long delta, CancellationToken ct = default);

    /// <summary>
    /// Takes <paramref name="amount"/> off a player's score, but never below zero — unlike
    /// <see cref="AddAsync"/>, which a negative delta could otherwise send underwater. Implemented so
    /// that two penalties arriving for the same player at once cannot race past the floor.
    /// </summary>
    Task PenaliseAsync(string playerId, long amount, CancellationToken ct = default);
    Task<IReadOnlyList<LeaderboardEntry>> TopAsync(int count, CancellationToken ct = default);
    Task<IReadOnlyList<LeaderboardEntry>> AmongAsync(IReadOnlyCollection<string> playerIds, CancellationToken ct = default);
}
