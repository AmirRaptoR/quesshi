using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Application.UseCases;



/// <summary>
/// Composes the questions of a duel: the chosen categories in rotation, difficulty ramping easy →
/// hard across the whole run. Falls back rather than failing, because an empty bucket in one topic
/// should not stop a match.
/// </summary>
public sealed class QuestionSetBuilder(IQuestionRepository questions, ICategoryRepository categories)
{
    public async Task<IReadOnlyList<Question>> BuildAsync(Language lang, IReadOnlyList<string>? preferred = null,
        int? questionCount = null, IReadOnlyList<Difficulty>? levels = null, CancellationToken ct = default)
    {
        // Choosing is a promise, and it binds the fallbacks. Asking for one category and being handed
        // ten questions from another is not a thin bucket quietly coping, it is a different duel with
        // the right label on it — and nothing on screen would say so.
        var allowed = levels is { Count: > 0 } ? [.. levels.Distinct().Order()] : MatchRules.AllLevels;
        var count = questionCount ?? MatchRules.QuestionsPerMatch;
        if (!MatchRules.IsValidCount(count)) count = MatchRules.QuestionsPerMatch;

        var active = (await categories.AllAsync(ct)).Where(c => c.IsActive).ToList();
        if (active.Count == 0) throw new NotEnoughQuestionsException("There are no active categories.");

        var chosen = ChooseCategories(active, preferred);

        // Named categories are the only ones in play; unnamed means the whole bank is fair game.
        var fallbackTo = preferred is { Count: > 0 } ? chosen : active;
        var picked = new List<Question>(count);
        var used = new HashSet<string>();

        for (var slot = 0; slot < count; slot++)
        {
            var category = chosen[slot % chosen.Count];
            var level = MatchRules.LevelForSlot(slot, count, allowed);

            var question = await TakeAsync(lang, category.Id, level, used, ct)
                        ?? await TakeAnyLevelAsync(lang, category.Id, allowed, used, ct)
                        ?? await TakeAnywhereAsync(lang, fallbackTo, allowed, used, ct)
                        ?? throw new NotEnoughQuestionsException(
                               $"Not enough approved {lang} questions in {string.Join(", ", fallbackTo.Select(c => c.Id))} " +
                               $"to fill a match (got {picked.Count} of {count}).");

            picked.Add(question);
            used.Add(question.Id);
        }

        return picked;
    }

    /// <summary>
    /// A player who named categories gets exactly those and nothing else — that is the whole point
    /// of asking. Otherwise three are drawn at random, which is the classic duel.
    /// </summary>
    private static List<Category> ChooseCategories(List<Category> active, IReadOnlyList<string>? preferred)
    {
        var chosen = new List<Category>();

        if (preferred is not null)
            foreach (var id in preferred.Distinct())
                if (active.FirstOrDefault(c => c.Id == id) is { } category)
                    chosen.Add(category);

        if (chosen.Count > 0) return chosen;

        foreach (var c in active.OrderBy(_ => Random.Shared.Next()))
        {
            if (chosen.Count == MatchRules.CategoriesPerMatch) break;
            chosen.Add(c);
        }

        return chosen;
    }

    private async Task<Question?> TakeAsync(Language lang, string categoryId, Difficulty level, HashSet<string> used, CancellationToken ct)
        => (await questions.SampleApprovedAsync(lang, categoryId, level, 1, used, ct)).FirstOrDefault();

    private async Task<Question?> TakeAnyLevelAsync(Language lang, string categoryId,
        IReadOnlyList<Difficulty> allowed, HashSet<string> used, CancellationToken ct)
    {
        foreach (var level in allowed)
            if (await TakeAsync(lang, categoryId, level, used, ct) is { } q) return q;
        return null;
    }

    private async Task<Question?> TakeAnywhereAsync(Language lang, List<Category> active,
        IReadOnlyList<Difficulty> allowed, HashSet<string> used, CancellationToken ct)
    {
        foreach (var category in active.OrderBy(_ => Random.Shared.Next()))
            if (await TakeAnyLevelAsync(lang, category.Id, allowed, used, ct) is { } q) return q;
        return null;
    }
}
