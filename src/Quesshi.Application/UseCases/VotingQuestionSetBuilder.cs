using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Application.UseCases;

/// <summary>Builds voting sets only within the category scope selected by the application boundary.</summary>
public sealed class VotingQuestionSetBuilder(IVotingQuestionRepository questions)
{
    public async Task<IReadOnlyList<VotingQuestion>> BuildAsync(Language lang, ContentScope? scope = null,
        int? questionCount = null, CancellationToken ct = default)
    {
        scope ??= ContentScope.All;
        var count = questionCount ?? MatchRules.QuestionsPerMatch;
        if (!MatchRules.IsValidCount(count)) count = MatchRules.QuestionsPerMatch;
        if (scope.CategoryIds is { Count: 0 })
            throw new NotEnoughQuestionsException("The category scope is empty.");

        var categoryIds = scope.CategoryIds?.Distinct(StringComparer.Ordinal).ToList();
        var picked = new List<VotingQuestion>(count);
        var used = new HashSet<string>();
        for (var slot = 0; slot < count; slot++)
        {
            var preferred = categoryIds is { Count: > 0 }
                ? new ContentScope([categoryIds[slot % categoryIds.Count]])
                : scope;
            var question = (await questions.SampleApprovedAsync(lang, preferred, 1, used, ct)).FirstOrDefault();
            if (question is null && preferred != scope)
                question = (await questions.SampleApprovedAsync(lang, scope, 1, used, ct)).FirstOrDefault();
            if (question is null)
                throw new NotEnoughQuestionsException($"Not enough approved {lang} voting questions in the supplied category scope (got {picked.Count} of {count}).");
            picked.Add(question);
            used.Add(question.Id);
        }
        return picked;
    }
}
