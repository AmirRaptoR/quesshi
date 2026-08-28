using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Server.Tests;

public sealed class FakeArchive : IMatchArchive
{
    public readonly List<ArchivedMatch> Items = [];

    /// <summary>Counterpart to FakePlayers.Queries; the list endpoint should read the archive once.</summary>
    public int Queries;

    /// <summary>Stands in for the round trip to Mongo, as in FakePlayers.</summary>
    public int DelayMs;

    public void ResetCounters() => Queries = 0;
    public Task SaveAsync(ArchivedMatch m, CancellationToken ct = default) { Items.RemoveAll(x => x.Id == m.Id); Items.Add(m); return Task.CompletedTask; }
    public Task<ArchivedMatch?> ByCodeAsync(string code, CancellationToken ct = default) => Task.FromResult(Items.FirstOrDefault(m => m.Code == code));
    public async Task<IReadOnlyList<ArchivedMatch>> ForPlayerAsync(string p, int take, CancellationToken ct = default)
    {
        Queries++;
        if (DelayMs > 0) await Task.Delay(DelayMs, ct);
        return [.. Items.Where(m => m.ChallengerId == p || m.OpponentId == p).OrderByDescending(m => m.CreatedAt).Take(take)];
    }
    public Task<long> CountAsync(CancellationToken ct = default) => Task.FromResult((long)Items.Count);
}
