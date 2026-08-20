using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Application.Tests;

public sealed class ScriptedGenerator(params GeneratedQuestion[] batch) : IQuestionGenerator
{
    public bool IsConfigured { get; set; } = true;
    public int Calls { get; private set; }

    /// <summary>Every language the bank asked about, in order. Each entry is one paid call.</summary>
    public List<Language> Languages { get; } = [];
    public Task<IReadOnlyList<GeneratedQuestion>> GenerateAsync(Language lang, Category category, Difficulty level, int count, IReadOnlyCollection<string> avoid, CancellationToken ct = default)
    {
        Calls++;
        Languages.Add(lang);
        return Task.FromResult<IReadOnlyList<GeneratedQuestion>>([.. batch]);
    }

    public Task<IReadOnlyList<GeneratedQuestion>> GenerateIllustratedAsync(Language lang, Category category, Difficulty level, int count, IReadOnlyCollection<string> avoid, CancellationToken ct = default)
    {
        Calls++;
        Languages.Add(lang);
        return Task.FromResult<IReadOnlyList<GeneratedQuestion>>([.. batch]);
    }
}
