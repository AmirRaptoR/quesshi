using Quesshi.Application.Ports;

namespace Quesshi.Application.Tests;

public sealed class InMemoryVotingGenerationLog : IVotingGenerationLog
{
    public readonly List<VotingGenerationRun> Runs = [];

    public Task SaveAsync(VotingGenerationRun run, CancellationToken ct = default)
    {
        Runs.RemoveAll(r => r.Id == run.Id);
        Runs.Add(run);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VotingGenerationRun>> RecentAsync(int take,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<VotingGenerationRun>>([.. Runs
            .OrderByDescending(r => r.StartedAt).Take(take)]);
}
