using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Application.Tests;

public sealed class ScriptedVotingGenerator(params GeneratedVotingQuestion[] batch)
    : IVotingQuestionGenerator
{
    public bool IsConfigured { get; set; } = true;
    public int Calls { get; private set; }
    public Language? LastLanguage { get; private set; }
    public string? LastCategoryId { get; private set; }
    public VotingAnswerSource? LastAnswerSource { get; private set; }
    public int LastCount { get; private set; }
    public IReadOnlyCollection<string> LastAvoid { get; private set; } = [];

    public Task<IReadOnlyList<GeneratedVotingQuestion>> GenerateAsync(Language lang,
        VotingCategory category, VotingAnswerSource answerSource, int count,
        IReadOnlyCollection<string> avoid, CancellationToken ct = default)
    {
        Calls++;
        LastLanguage = lang;
        LastCategoryId = category.Id;
        LastAnswerSource = answerSource;
        LastCount = count;
        LastAvoid = avoid;
        return Task.FromResult<IReadOnlyList<GeneratedVotingQuestion>>([.. batch]);
    }
}
