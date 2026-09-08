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

    public void ResetCounters() => Queries = 0;

    public async Task<Player?> GetAsync(string id, CancellationToken ct = default)
    {
        Queries++;
        if (DelayMs > 0) await Task.Delay(DelayMs, ct);
        var found = Items.FirstOrDefault(p => p.Id == id);

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
        return [.. Items.Where(p => ids.Contains(p.Id))];
    }
    public Task<Player?> GetByEmailAsync(string e, CancellationToken ct = default) => Task.FromResult(Items.FirstOrDefault(p => p.Email == e));
    public Task<IReadOnlyList<Player>> SearchAsync(string? t, int s, int k, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Player>>([.. Items]);
    public Task<long> CountAsync(CancellationToken ct = default) => Task.FromResult((long)Items.Count);

    public Task UpsertAsync(Player p, CancellationToken ct = default)
    {
        if (FailNextUpsertFor.Remove(p.Id)) throw new InvalidOperationException($"Simulated write failure for {p.Id}.");
        Items.RemoveAll(x => x.Id == p.Id);
        Items.Add(p);
        return Task.CompletedTask;
    }
}
