using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Server.Tests;

public sealed class FakeLeaderboard : ILeaderboard
{
    public readonly Dictionary<string, long> Scores = [];
    private readonly Lock _gate = new();

    public Task AddAsync(string playerId, long delta, CancellationToken ct = default)
    {
        Scores[playerId] = Scores.GetValueOrDefault(playerId) + delta;
        return Task.CompletedTask;
    }

    // Locked for the same reason RedisLeaderboard reaches for a Lua script: a bare read-then-write
    // lets two concurrent penalties both read the score before either writes it back, and one of
    // them wins for nothing. A player with no entry stays without one — same as the Redis script's
    // ZSCORE-returns-nil check — so penalising can never be how a missing member joins the board.
    public Task PenaliseAsync(string playerId, long amount, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (Scores.TryGetValue(playerId, out var current))
                Scores[playerId] = Math.Max(0, current - amount);
        }
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<LeaderboardEntry>> TopAsync(int count, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LeaderboardEntry>>([.. Scores.OrderByDescending(kv => kv.Value).Select((kv, i) => new LeaderboardEntry(kv.Key, kv.Value, i + 1))]);
    public Task<IReadOnlyList<LeaderboardEntry>> AmongAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) => TopAsync(100, ct);
}
