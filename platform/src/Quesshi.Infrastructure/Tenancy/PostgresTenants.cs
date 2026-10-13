using System.Collections.Concurrent;
using Quesshi.Application.Tenancy;
using Quesshi.Infrastructure.Database;

namespace Quesshi.Infrastructure.Tenancy;

/// <summary>Looks up global host mappings through the dedicated system pool.</summary>
public sealed class PostgresTenants : ITenants
{
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(5);
    private const int DefaultCacheCapacity = 1024;

    private readonly DatabaseSources _sources;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _cacheLifetime;
    private readonly int _cacheCapacity;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<CacheEntry>>> _refreshes = new(StringComparer.Ordinal);
    private readonly object _cacheWriteLock = new();

    public PostgresTenants(
        DatabaseSources sources,
        TimeProvider? timeProvider = null,
        TimeSpan? cacheLifetime = null,
        int cacheCapacity = DefaultCacheCapacity)
    {
        _sources = sources;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cacheLifetime = cacheLifetime ?? CacheLifetime;
        if (_cacheLifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(cacheLifetime));
        if (cacheCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(cacheCapacity));
        _cacheCapacity = cacheCapacity;
    }

    public async Task<Tenant?> ResolveAsync(string host, CancellationToken cancellationToken = default)
    {
        var canonicalHost = TenantHost.Normalize(host);
        if (canonicalHost.Length == 0) return null;

        var now = _timeProvider.GetUtcNow();
        if (_cache.TryGetValue(canonicalHost, out var cached) && cached.ExpiresAt > now) return cached.Tenant;

        var refresh = new Lazy<Task<CacheEntry>>(
            () => RefreshAsync(canonicalHost),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var scheduledRefresh = _refreshes.GetOrAdd(canonicalHost, refresh);
        var refreshTask = scheduledRefresh.Value;
        if (ReferenceEquals(refresh, scheduledRefresh))
        {
            _ = refreshTask.ContinueWith(
                _ => RemoveRefresh(canonicalHost, scheduledRefresh),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return (await refreshTask.WaitAsync(cancellationToken)).Tenant;
    }

    private async Task<CacheEntry> RefreshAsync(string host)
    {
        var tenant = await QueryAsync(host, CancellationToken.None);
        var entry = new CacheEntry(tenant, _timeProvider.GetUtcNow().Add(_cacheLifetime));
        Cache(host, entry);
        return entry;
    }

    private void RemoveRefresh(string host, Lazy<Task<CacheEntry>> refresh)
    {
        if (_refreshes.TryGetValue(host, out var current) && ReferenceEquals(current, refresh))
        {
            _refreshes.TryRemove(host, out _);
        }
    }

    private void Cache(string host, CacheEntry entry)
    {
        lock (_cacheWriteLock)
        {
            foreach (var (cachedHost, cachedEntry) in _cache)
            {
                if (cachedEntry.ExpiresAt <= entry.ExpiresAt - _cacheLifetime) _cache.TryRemove(cachedHost, out _);
            }

            if (!_cache.ContainsKey(host) && _cache.Count >= _cacheCapacity)
            {
                var oldest = _cache.MinBy(pair => pair.Value.ExpiresAt).Key;
                _cache.TryRemove(oldest, out _);
            }

            _cache[host] = entry;
        }
    }

    private async Task<Tenant?> QueryAsync(string host, CancellationToken cancellationToken)
    {
        await using var connection = await _sources.System.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select tenants.id, tenants.slug, tenants.name, tenants.default_language
            from tenant_hosts
            join tenants on tenants.id = tenant_hosts.tenant_id
            where tenant_hosts.host = @host
            """;
        command.Parameters.AddWithValue("host", host);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new Tenant(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))
            : null;
    }

    private sealed record CacheEntry(Tenant? Tenant, DateTimeOffset ExpiresAt);
}
