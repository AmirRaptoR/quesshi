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

    public void ResetCounters() => Queries = 0;

    public async Task<Player?> GetAsync(string id, CancellationToken ct = default)
    {
        Queries++;
        if (DelayMs > 0) await Task.Delay(DelayMs, ct);
        return Items.FirstOrDefault(p => p.Id == id);
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
    public Task UpsertAsync(Player p, CancellationToken ct = default) { Items.RemoveAll(x => x.Id == p.Id); Items.Add(p); return Task.CompletedTask; }
}
