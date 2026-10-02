namespace Quesshi.Application.Ports;

public interface IVotingGenerationLog
{
    Task SaveAsync(VotingGenerationRun run, CancellationToken ct = default);
    Task<IReadOnlyList<VotingGenerationRun>> RecentAsync(int take, CancellationToken ct = default);
}
