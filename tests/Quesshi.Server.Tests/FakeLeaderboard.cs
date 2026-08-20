using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Server.Tests;

public sealed class FakeLeaderboard : ILeaderboard
{
    public readonly Dictionary<string, long> Scores = [];
    public Task AddAsync(string playerId, long delta, CancellationToken ct = default)
    {
        Scores[playerId] = Scores.GetValueOrDefault(playerId) + delta;
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<LeaderboardEntry>> TopAsync(int count, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LeaderboardEntry>>([.. Scores.OrderByDescending(kv => kv.Value).Select((kv, i) => new LeaderboardEntry(kv.Key, kv.Value, i + 1))]);
    public Task<IReadOnlyList<LeaderboardEntry>> AmongAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) => TopAsync(100, ct);
}
