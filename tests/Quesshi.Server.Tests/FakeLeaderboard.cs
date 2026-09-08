using Quesshi.Application.Ports;

namespace Quesshi.Server.Tests;

public sealed class FakeLeaderboard : ILeaderboard
{
    public readonly Dictionary<string, long> Scores = [];

    /// <summary>
    /// Player ids whose next <see cref="SetAsync"/> throws instead of writing, then reverts to
    /// succeeding — stands in for a Redis write failing after the Mongo write it followed already
    /// committed, so a test can assert the next call repairs it.
    /// </summary>
    public readonly HashSet<string> FailNextSetFor = [];

    /// <summary>Guards every touch of <see cref="Scores"/> — see <c>FakeArchive</c>'s own remarks on why
    /// a plain <see cref="Dictionary{TKey, TValue}"/> is not safe once settlement (issue #48) can call
    /// in from more than one grain activation at a time within a shared test collection.</summary>
    private readonly object _lock = new();

    public Task SetAsync(string playerId, long total, CancellationToken ct = default)
    {
        if (FailNextSetFor.Remove(playerId)) throw new InvalidOperationException($"Simulated write failure for {playerId}.");
        lock (_lock) Scores[playerId] = total;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<LeaderboardEntry>> TopAsync(int count, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<LeaderboardEntry>>([.. Scores.OrderByDescending(kv => kv.Value).Select((kv, i) => new LeaderboardEntry(kv.Key, kv.Value, i + 1))]);
    }
    public Task<IReadOnlyList<LeaderboardEntry>> AmongAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) => TopAsync(100, ct);
}
