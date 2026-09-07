using Quesshi.Application.Ports;

namespace Quesshi.Server.Tests;

/// <summary>In-memory stand-in for <see cref="IPresence"/>, counting calls so the single-bulk-read
/// criterion is provable without a real Redis. The hub writes this from a connection-handling thread
/// while a test reads it from the test thread, so every access — including the waiters below — goes
/// through <see cref="_gate"/> rather than trusting the backing <see cref="Dictionary{TKey,TValue}"/>
/// to be thread-safe on its own.</summary>
public sealed class FakePresence : IPresence
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _online = [];
    private readonly Dictionary<string, TaskCompletionSource> _onlineWaiters = [];
    private readonly Dictionary<string, TaskCompletionSource> _offlineWaiters = [];
    private int _onlineCalls;

    /// <summary>How many times <see cref="OnlineAsync"/> has been called, whatever the id count.</summary>
    public int OnlineCalls { get { lock (_gate) return _onlineCalls; } }

    public bool IsOnline(string playerId)
    {
        lock (_gate) return _online.ContainsKey(playerId);
    }

    public Task MarkOnlineAsync(string playerId, TimeSpan ttl, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _online[playerId] = DateTimeOffset.UtcNow + ttl;
            if (_onlineWaiters.Remove(playerId, out var waiter)) waiter.TrySetResult();
        }

        return Task.CompletedTask;
    }

    public Task MarkOfflineAsync(string playerId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _online.Remove(playerId);
            if (_offlineWaiters.Remove(playerId, out var waiter)) waiter.TrySetResult();
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<string>> OnlineAsync(IReadOnlyCollection<string> playerIds, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _onlineCalls++;
            return Task.FromResult<IReadOnlyCollection<string>>([.. playerIds.Where(_online.ContainsKey)]);
        }
    }

    /// <summary>Waits until <paramref name="playerId"/> is online — completing immediately if
    /// <see cref="MarkOnlineAsync"/> has already run for them — or throws once <paramref name="timeout"/>
    /// elapses. Lets a test assert on the hub's <c>OnConnectedAsync</c> having actually completed instead
    /// of on <c>HubConnection.StartAsync</c> having returned, which races it.</summary>
    public Task WaitForOnlineAsync(string playerId, TimeSpan timeout)
        => WaitAsync(playerId, _onlineWaiters, timeout, wantOnline: true);

    /// <summary>Waits until <paramref name="playerId"/> is offline — completing immediately if
    /// <see cref="MarkOfflineAsync"/> has already run for them — or throws once <paramref name="timeout"/>
    /// elapses. Lets a test assert on the hub's <c>OnDisconnectedAsync</c> having actually completed
    /// instead of on a fixed delay having elapsed.</summary>
    public Task WaitForOfflineAsync(string playerId, TimeSpan timeout)
        => WaitAsync(playerId, _offlineWaiters, timeout, wantOnline: false);

    private async Task WaitAsync(string playerId, Dictionary<string, TaskCompletionSource> waiters, TimeSpan timeout, bool wantOnline)
    {
        Task task;
        lock (_gate)
        {
            var alreadyThere = wantOnline ? _online.ContainsKey(playerId) : !_online.ContainsKey(playerId);
            if (alreadyThere) return;

            if (!waiters.TryGetValue(playerId, out var waiter))
            {
                waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                waiters[playerId] = waiter;
            }

            task = waiter.Task;
        }

        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (completed != task)
            throw new TimeoutException(
                $"Timed out after {timeout} waiting for '{playerId}' to become {(wantOnline ? "online" : "offline")}.");
    }
}
