using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Application.Tests;

public sealed class InMemoryGenerationLog : IGenerationLog
{
    public readonly List<GenerationRun> Runs = [];
    public Task SaveAsync(GenerationRun r, CancellationToken ct = default) { Runs.RemoveAll(x => x.Id == r.Id); Runs.Add(r); return Task.CompletedTask; }
    public Task<IReadOnlyList<GenerationRun>> RecentAsync(int take, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<GenerationRun>>([.. Runs.OrderByDescending(r => r.StartedAt).Take(take)]);
}
