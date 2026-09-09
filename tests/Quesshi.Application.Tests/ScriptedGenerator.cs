using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Application.Tests;

/// <summary>
/// A generator that answers with whatever a test scripted. The three kinds have three separate
/// scripts, because a test that says "the model returns these two sorting questions" must not also
/// be saying "and the same two arrive as choice questions" — the pipeline asks each kind its own
/// question and this has to be able to answer them differently, including with nothing at all.
/// </summary>
public sealed class ScriptedGenerator(params GeneratedQuestion[] batch) : IQuestionGenerator
{
    public bool IsConfigured { get; set; } = true;
    public int Calls { get; private set; }

    /// <summary>Every language the bank asked about, in order. Each entry is one paid call.</summary>
    public List<Language> Languages { get; } = [];

    /// <summary>Every kind the bank asked for, in order — what proves a run tried for sorts at all.</summary>
    public List<QuestionKind> Kinds { get; } = [];

    /// <summary>What a sorting request comes back with. Empty unless a test fills it.</summary>
    public List<GeneratedQuestion> Sorts { get; } = [];

    /// <summary>What a map request comes back with. Empty unless a test fills it.</summary>
    public List<GeneratedQuestion> Maps { get; } = [];

    public Task<IReadOnlyList<GeneratedQuestion>> GenerateAsync(Language lang, Category category, Difficulty level, int count, IReadOnlyCollection<string> avoid, CancellationToken ct = default)
        => Answer(lang, QuestionKind.Choice, [.. batch]);

    public Task<IReadOnlyList<GeneratedQuestion>> GenerateIllustratedAsync(Language lang, Category category, Difficulty level, int count, IReadOnlyCollection<string> avoid, CancellationToken ct = default)
        => Answer(lang, QuestionKind.Choice, [.. batch]);

    public Task<IReadOnlyList<GeneratedQuestion>> GenerateSortAsync(Language lang, Category category, Difficulty level, int count, IReadOnlyCollection<string> avoid, CancellationToken ct = default)
        => Answer(lang, QuestionKind.Sort, [.. Sorts]);

    public Task<IReadOnlyList<GeneratedQuestion>> GenerateMapAsync(Language lang, Category category, Difficulty level, int count, IReadOnlyCollection<string> avoid, CancellationToken ct = default)
        => Answer(lang, QuestionKind.Map, [.. Maps]);

    private Task<IReadOnlyList<GeneratedQuestion>> Answer(Language lang, QuestionKind kind, List<GeneratedQuestion> scripted)
    {
        Calls++;
        Languages.Add(lang);
        Kinds.Add(kind);

        return Task.FromResult<IReadOnlyList<GeneratedQuestion>>(scripted);
    }
}
