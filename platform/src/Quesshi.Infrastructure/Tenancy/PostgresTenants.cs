using System.Collections.Concurrent;
using Quesshi.Application.Tenancy;
using Quesshi.Infrastructure.Database;

namespace Quesshi.Infrastructure.Tenancy;

/// <summary>Looks up global host mappings through the dedicated system pool.</summary>
public sealed class PostgresTenants : ITenants
{
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(5);

    private readonly DatabaseSources _sources;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _cacheLifetime;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public PostgresTenants(DatabaseSources sources, TimeProvider? timeProvider = null, TimeSpan? cacheLifetime = null)
    {
        _sources = sources;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cacheLifetime = cacheLifetime ?? CacheLifetime;
        if (_cacheLifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(cacheLifetime));
    }

    public async Task<Tenant?> ResolveAsync(string host, CancellationToken cancellationToken = default)
    {
        var canonicalHost = TenantHost.Normalize(host);
        if (canonicalHost.Length == 0) return null;

        var now = _timeProvider.GetUtcNow();
        if (_cache.TryGetValue(canonicalHost, out var cached) && cached.ExpiresAt > now) return cached.Tenant;

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            now = _timeProvider.GetUtcNow();
            if (_cache.TryGetValue(canonicalHost, out cached) && cached.ExpiresAt > now) return cached.Tenant;

            var tenant = await QueryAsync(canonicalHost, cancellationToken);
            _cache[canonicalHost] = new CacheEntry(tenant, now.Add(_cacheLifetime));
            return tenant;
        }
        finally
        {
            _refreshLock.Release();
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
