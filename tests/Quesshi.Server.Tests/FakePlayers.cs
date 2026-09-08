using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Server.Tests;

public sealed class FakePlayers : IPlayerRepository
{
    public readonly List<Player> Items = [];

    /// <summary>How many round trips a test has cost, so "one query however many matches" can be asserted.</summary>
    public int Queries;

    /// <summary>Stands in for the round trip to Mongo, so a benchmark can measure what latency costs.</summary>
    public int DelayMs;

    /// <summary>
    /// Player ids whose next <see cref="UpsertAsync"/> throws instead of writing, then reverts to
    /// succeeding — a test's way of simulating a single Mongo write failing without touching any
    /// other player's. Consumed on use so a retry after the failure succeeds normally.
    /// </summary>
    public readonly HashSet<string> FailNextUpsertFor = [];

    /// <summary>Guards every touch of <see cref="Items"/> — see <c>FakeArchive</c>'s own remarks on why
    /// a plain <see cref="List{T}"/> is not safe once settlement (issue #48) can call in from more than
    /// one grain activation at a time within a shared test collection.</summary>
    private readonly object _lock = new();

    public void ResetCounters() => Queries = 0;

    public async Task<Player?> GetAsync(string id, CancellationToken ct = default)
    {
        Queries++;
        if (DelayMs > 0) await Task.Delay(DelayMs, ct);
        Player? found;
        lock (_lock) found = Items.FirstOrDefault(p => p.Id == id);

        // A snapshot round trip rather than the live reference: Player is a mutable domain object, so
        // handing back the same instance stored in Items would make an in-memory mutation visible to
        // every future read regardless of whether the UpsertAsync meant to persist it ever succeeded —
        // exactly the gap PlayerGrain's cache-invalidation-on-a-failed-write behaviour needs a real
        // failure to exercise. A real repository round-trips through serialisation for the same reason.
        return found is null ? null : Player.FromSnapshot(found.ToSnapshot());
    }

    public async Task<IReadOnlyList<Player>> GetManyAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        Queries++;
        if (DelayMs > 0) await Task.Delay(DelayMs, ct);
        lock (_lock) return [.. Items.Where(p => ids.Contains(p.Id))];
    }
    public Task<Player?> GetByEmailAsync(string e, CancellationToken ct = default) { lock (_lock) return Task.FromResult(Items.FirstOrDefault(p => p.Email == e)); }
    public Task<IReadOnlyList<Player>> SearchAsync(string? t, int s, int k, CancellationToken ct = default) { lock (_lock) return Task.FromResult<IReadOnlyList<Player>>([.. Items]); }
    public Task<long> CountAsync(CancellationToken ct = default) { lock (_lock) return Task.FromResult((long)Items.Count); }

    public Task UpsertAsync(Player p, CancellationToken ct = default)
    {
        if (FailNextUpsertFor.Remove(p.Id)) throw new InvalidOperationException($"Simulated write failure for {p.Id}.");
        lock (_lock) { Items.RemoveAll(x => x.Id == p.Id); Items.Add(p); }
        return Task.CompletedTask;
    }
}
