namespace Quesshi.Application.Ports;

public interface ILeaderboard
{
    Task AddAsync(string playerId, long delta, CancellationToken ct = default);
    Task<IReadOnlyList<LeaderboardEntry>> TopAsync(int count, CancellationToken ct = default);
    Task<IReadOnlyList<LeaderboardEntry>> AmongAsync(IReadOnlyCollection<string> playerIds, CancellationToken ct = default);
}
