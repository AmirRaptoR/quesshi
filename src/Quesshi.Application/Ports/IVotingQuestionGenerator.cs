using Quesshi.Domain;

namespace Quesshi.Application.Ports;

/// <summary>AI provider boundary for scoreless voting content, separate from trivia generation.</summary>
public interface IVotingQuestionGenerator
{
    bool IsConfigured { get; }
    Task<IReadOnlyList<GeneratedVotingQuestion>> GenerateAsync(Language lang,
        Category category, VotingAnswerSource answerSource, int count,
        IReadOnlyCollection<string> avoid, CancellationToken ct = default);
}
