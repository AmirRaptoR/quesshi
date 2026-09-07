using Quesshi.Application.Ports;

namespace Quesshi.Server.Tests;

/// <summary>In-memory stand-in for <see cref="IPresence"/>, counting calls so the single-bulk-read
/// criterion is provable without a real Redis.</summary>
public sealed class FakePresence : IPresence
{
    private readonly Dictionary<string, DateTimeOffset> _online = [];

    /// <summary>How many times <see cref="OnlineAsync"/> has been called, whatever the id count.</summary>
    public int OnlineCalls;

    public bool IsOnline(string playerId) => _online.ContainsKey(playerId);

    public Task MarkOnlineAsync(string playerId, TimeSpan ttl, CancellationToken ct = default)
    {
        _online[playerId] = DateTimeOffset.UtcNow + ttl;
        return Task.CompletedTask;
    }

    public Task MarkOfflineAsync(string playerId, CancellationToken ct = default)
    {
        _online.Remove(playerId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<string>> OnlineAsync(IReadOnlyCollection<string> playerIds, CancellationToken ct = default)
    {
        OnlineCalls++;
        return Task.FromResult<IReadOnlyCollection<string>>([.. playerIds.Where(_online.ContainsKey)]);
    }
}
