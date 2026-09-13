using Quesshi.Domain;

namespace Quesshi.Application.Ports;

/// <summary>AI provider boundary for scoreless matching content, separate from trivia generation.</summary>
public interface IMatchingQuestionGenerator
{
    bool IsConfigured { get; }
    Task<IReadOnlyList<GeneratedMatchingQuestion>> GenerateAsync(Language lang,
        MatchingCategory category, MatchingAnswerSource answerSource, int count,
        IReadOnlyCollection<string> avoid, CancellationToken ct = default);
}
