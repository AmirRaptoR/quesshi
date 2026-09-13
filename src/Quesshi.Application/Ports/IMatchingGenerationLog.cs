namespace Quesshi.Application.Ports;

public interface IMatchingGenerationLog
{
    Task SaveAsync(MatchingGenerationRun run, CancellationToken ct = default);
    Task<IReadOnlyList<MatchingGenerationRun>> RecentAsync(int take, CancellationToken ct = default);
}
