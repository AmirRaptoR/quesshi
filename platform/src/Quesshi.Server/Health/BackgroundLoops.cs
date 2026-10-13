namespace Quesshi.Server.Health;

public sealed record BackgroundLoopStatus(string Name, TimeSpan? LastSuccessfulPassAge)
{
    public bool IsHealthy => LastSuccessfulPassAge is { } age && age < TimeSpan.FromSeconds(30);
}

public interface IBackgroundLoopRegistry
{
    IBackgroundLoop Register(string name);
    IReadOnlyList<BackgroundLoopStatus> Snapshot();
}

public interface IBackgroundLoop
{
    void Passed();
}

public sealed class BackgroundLoopRegistry(TimeProvider? timeProvider = null) : IBackgroundLoopRegistry
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, DateTimeOffset?> _passes = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public IBackgroundLoop Register(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_lock)
        {
            if (!_passes.TryAdd(name, null)) throw new InvalidOperationException($"Background loop '{name}' is already registered.");
        }
        return new Registration(this, name);
    }

    public IReadOnlyList<BackgroundLoopStatus> Snapshot()
    {
        lock (_lock)
        {
            var now = _timeProvider.GetUtcNow();
            return _passes.Select(pair => new BackgroundLoopStatus(pair.Key, pair.Value is { } passed ? now - passed : null)).ToArray();
        }
    }

    private void Passed(string name)
    {
        lock (_lock) _passes[name] = _timeProvider.GetUtcNow();
    }

    private sealed class Registration(BackgroundLoopRegistry registry, string name) : IBackgroundLoop
    {
        public void Passed() => registry.Passed(name);
    }
}
