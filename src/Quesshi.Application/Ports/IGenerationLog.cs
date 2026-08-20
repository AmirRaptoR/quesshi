namespace Quesshi.Application.Ports;

public interface IGenerationLog
{
    Task SaveAsync(GenerationRun run, CancellationToken ct = default);
    Task<IReadOnlyList<GenerationRun>> RecentAsync(int take, CancellationToken ct = default);
}
