using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Application.UseCases;

/// <summary>Builds a question set exclusively from the matching content bank.</summary>
public sealed class MatchingQuestionSetBuilder(
    IMatchingQuestionRepository questions,
    IMatchingCategoryRepository categories)
{
    public async Task<IReadOnlyList<MatchingQuestion>> BuildAsync(Language lang,
        IReadOnlyList<string>? categoryIds = null, int? questionCount = null,
        CancellationToken ct = default)
    {
        var count = questionCount ?? MatchRules.QuestionsPerMatch;
        if (!MatchRules.IsValidCount(count)) count = MatchRules.QuestionsPerMatch;

        var active = (await categories.AllAsync(ct)).Where(c => c.IsActive).ToList();
        if (active.Count == 0)
            throw new NotEnoughQuestionsException("There are no active categories.");

        var chosen = ChooseCategories(active, categoryIds);
        var fallbackTo = categoryIds is { Count: > 0 } && chosen.Count > 0 ? chosen : active;
        var picked = new List<MatchingQuestion>(count);
        var used = new HashSet<string>();

        for (var slot = 0; slot < count; slot++)
        {
            var category = chosen[slot % chosen.Count];
            var question = await TakeAsync(lang, category.Id, used, ct)
                ?? await TakeAnywhereAsync(lang, fallbackTo, used, ct)
                ?? throw new NotEnoughQuestionsException(
                    $"Not enough approved {lang} matching questions in {string.Join(", ", fallbackTo.Select(c => c.Id))} " +
                    $"to fill a match (got {picked.Count} of {count}).");

            picked.Add(question);
            used.Add(question.Id);
        }

        return picked;
    }

    private static List<MatchingCategory> ChooseCategories(List<MatchingCategory> active,
        IReadOnlyList<string>? requested)
    {
        var chosen = new List<MatchingCategory>();
        if (requested is { Count: > 0 })
            foreach (var id in requested.Distinct())
                if (active.FirstOrDefault(c => c.Id == id) is { } category)
                    chosen.Add(category);

        if (chosen.Count > 0) return chosen;

        foreach (var category in active.OrderBy(_ => Random.Shared.Next()))
        {
            if (chosen.Count == MatchRules.CategoriesPerMatch) break;
            chosen.Add(category);
        }

        return chosen;
    }

    private async Task<MatchingQuestion?> TakeAsync(Language lang, string categoryId,
        HashSet<string> used, CancellationToken ct)
        => (await questions.SampleApprovedAsync(lang, categoryId, 1, used, ct)).FirstOrDefault();

    private async Task<MatchingQuestion?> TakeAnywhereAsync(Language lang,
        IReadOnlyList<MatchingCategory> fallbackTo, HashSet<string> used, CancellationToken ct)
    {
        foreach (var category in fallbackTo.OrderBy(_ => Random.Shared.Next()))
            if (await TakeAsync(lang, category.Id, used, ct) is { } question)
                return question;
        return null;
    }
}
