using Quesshi.Application.Ports;

namespace Quesshi.Server.Tests;

/// <summary>In-memory stand-in for <see cref="ILiveDirectory"/>, the same shape as <see cref="FakeArchive"/>.</summary>
public sealed class FakeLiveDirectory : ILiveDirectory
{
    public readonly Dictionary<string, LiveDirectoryRow> Rows = [];

    /// <summary>When set, every call throws — for the "Redis unreachable degrades rather than breaks" test.</summary>
    public bool ThrowOnEveryCall { get; set; }

    public int ConnectedCount { get; set; }
    public int QueueDepth { get; set; }

    public Task UpsertAsync(LiveDirectoryRow row, CancellationToken ct = default)
    {
        if (ThrowOnEveryCall) throw new InvalidOperationException("Fake directory failure.");
        Rows[row.MatchId] = row;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string matchId, CancellationToken ct = default)
    {
        if (ThrowOnEveryCall) throw new InvalidOperationException("Fake directory failure.");
        Rows.Remove(matchId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<LiveDirectoryRow>> AllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LiveDirectoryRow>>([.. Rows.Values]);

    public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(Rows.Count);
    public Task<int> ConnectedCountAsync(CancellationToken ct = default) => Task.FromResult(ConnectedCount);
    public Task<int> QueueDepthAsync(CancellationToken ct = default) => Task.FromResult(QueueDepth);
}
