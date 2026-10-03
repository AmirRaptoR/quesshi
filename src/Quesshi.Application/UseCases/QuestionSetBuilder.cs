using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Application.UseCases;

/// <summary>Builds trivia sets only within the category scope selected by the application boundary.</summary>
public sealed class QuestionSetBuilder(IQuestionRepository questions)
{
    public async Task<IReadOnlyList<Question>> BuildAsync(Language lang, ContentScope? scope = null,
        int? questionCount = null, IReadOnlyList<Difficulty>? levels = null, CancellationToken ct = default)
    {
        scope ??= ContentScope.All;
        var allowed = levels is { Count: > 0 } ? [.. levels.Distinct().Order()] : MatchRules.AllLevels;
        var count = questionCount ?? MatchRules.QuestionsPerMatch;
        if (!MatchRules.IsValidCount(count)) count = MatchRules.QuestionsPerMatch;
        if (scope.CategoryIds is { Count: 0 })
            throw new NotEnoughQuestionsException("The category scope is empty.");

        var categoryIds = scope.CategoryIds?.Distinct(StringComparer.Ordinal).ToList();
        var picked = new List<Question>(count);
        var used = new HashSet<string>();
        for (var slot = 0; slot < count; slot++)
        {
            var level = MatchRules.LevelForSlot(slot, count, allowed);
            var preferred = categoryIds is { Count: > 0 }
                ? new ContentScope([categoryIds[slot % categoryIds.Count]])
                : scope;
            var question = await TakeAsync(lang, preferred, level, used, ct)
                ?? (preferred == scope ? null : await TakeAsync(lang, scope, level, used, ct));
            if (question is null)
                foreach (var fallbackLevel in allowed)
                {
                    question = await TakeAsync(lang, preferred, fallbackLevel, used, ct)
                        ?? (preferred == scope ? null : await TakeAsync(lang, scope, fallbackLevel, used, ct));
                    if (question is not null) break;
                }
            if (question is null)
                throw new NotEnoughQuestionsException($"Not enough approved {lang} questions in the supplied category scope (got {picked.Count} of {count}).");
            picked.Add(question);
            used.Add(question.Id);
        }
        return picked;
    }

    private async Task<Question?> TakeAsync(Language lang, ContentScope scope, Difficulty level,
        HashSet<string> used, CancellationToken ct)
        => (await questions.SampleApprovedAsync(lang, scope, level, 1, used, ct)).FirstOrDefault();
}
