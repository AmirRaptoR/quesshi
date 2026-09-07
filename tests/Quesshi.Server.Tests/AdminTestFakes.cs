using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Server.Tests;

/// <summary>Minimal stand-ins for the dashboard endpoint's spend/generation dependencies — nothing
/// AdminEndpointsTests exercises reads their content, only that the endpoint does not throw.</summary>
public sealed class FakeGenerationLog : IGenerationLog
{
    public Task SaveAsync(GenerationRun run, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<GenerationRun>> RecentAsync(int take, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<GenerationRun>>([]);
}

public sealed class FakeAiSpendLog : IAiSpendLog
{
    public Task RecordAsync(AiCall call, CancellationToken ct = default) => Task.CompletedTask;
    public Task<AiSpend> TotalsAsync(DateTimeOffset? since = null, CancellationToken ct = default) => Task.FromResult(AiSpend.Nothing);
}

public sealed class FakeQuestionGenerator : IQuestionGenerator
{
    public bool IsConfigured => false;
    public Task<IReadOnlyList<GeneratedQuestion>> GenerateAsync(Language lang, Category category, Difficulty level, int count, IReadOnlyCollection<string> avoid, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<GeneratedQuestion>>([]);
    public Task<IReadOnlyList<GeneratedQuestion>> GenerateIllustratedAsync(Language lang, Category category, Difficulty level, int count, IReadOnlyCollection<string> avoid, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<GeneratedQuestion>>([]);
}
