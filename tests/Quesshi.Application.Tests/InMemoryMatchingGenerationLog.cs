using Quesshi.Application.Ports;

namespace Quesshi.Application.Tests;

public sealed class InMemoryMatchingGenerationLog : IMatchingGenerationLog
{
    public readonly List<MatchingGenerationRun> Runs = [];

    public Task SaveAsync(MatchingGenerationRun run, CancellationToken ct = default)
    {
        Runs.RemoveAll(r => r.Id == run.Id);
        Runs.Add(run);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MatchingGenerationRun>> RecentAsync(int take,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MatchingGenerationRun>>([.. Runs
            .OrderByDescending(r => r.StartedAt).Take(take)]);
}
