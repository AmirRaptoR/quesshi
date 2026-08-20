using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Server.Tests;

public sealed class FakeArchive : IMatchArchive
{
    public readonly List<ArchivedMatch> Items = [];
    public Task SaveAsync(ArchivedMatch m, CancellationToken ct = default) { Items.RemoveAll(x => x.Id == m.Id); Items.Add(m); return Task.CompletedTask; }
    public Task<ArchivedMatch?> ByCodeAsync(string code, CancellationToken ct = default) => Task.FromResult(Items.FirstOrDefault(m => m.Code == code));
    public Task<IReadOnlyList<ArchivedMatch>> ForPlayerAsync(string p, int take, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ArchivedMatch>>([.. Items.Where(m => m.ChallengerId == p || m.OpponentId == p)]);
    public Task<long> CountAsync(CancellationToken ct = default) => Task.FromResult((long)Items.Count);
}
