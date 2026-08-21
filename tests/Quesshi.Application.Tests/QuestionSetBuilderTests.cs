using Quesshi.Application.UseCases;
using Quesshi.Domain;

namespace Quesshi.Application.Tests;

public class QuestionSetBuilderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
    private readonly InMemoryQuestions _questions = new();
    private readonly InMemoryCategories _categories = new();

    private QuestionSetBuilder Sut() => new(_questions, _categories);

    /// <summary>Fills every (category, level) bucket of <paramref name="lang"/> with n approved questions.</summary>
    private void Stock(int perBucket, Language lang = Language.En, params string[] categories)
    {
        foreach (var c in categories)
        {
            _categories.UpsertAsync(new Category(c, c, c, "*", "#fff"));
            foreach (var level in MatchRules.AllLevels)
                for (var i = 0; i < perBucket; i++)
                    _questions.UpsertAsync(Q($"{c}-{level}-{i}", lang, c, level));
        }
    }

    private static Question Q(string id, Language lang, string cat, Difficulty level, QuestionStatus status = QuestionStatus.Approved)
        => Question.Create(id, lang, cat, level, $"prompt {id}", ["a", "b", "c", "d"], 0, T0, status: status);

    [Fact]
    public async Task Builds_a_full_match_worth_of_questions()
    {
        Stock(3, Language.En, "geography", "movies", "history", "science");
        var set = await Sut().BuildAsync(Language.En);
        Assert.Equal(MatchRules.QuestionsPerMatch, set.Count);
    }

    [Fact]
    public async Task Never_repeats_a_question_inside_one_match()
    {
        Stock(3, Language.En, "geography", "movies", "history");
        var set = await Sut().BuildAsync(Language.En);
        Assert.Equal(set.Count, set.Select(q => q.Id).Distinct().Count());
    }

    [Fact]
    public async Task Draws_from_three_categories()
    {
        Stock(3, Language.En, "geography", "movies", "history", "science", "music");
        var set = await Sut().BuildAsync(Language.En);
        Assert.Equal(MatchRules.CategoriesPerMatch, set.Select(q => q.CategoryId).Distinct().Count());
    }

    [Fact]
    public async Task Difficulty_follows_the_ramp()
    {
        Stock(3, Language.En, "geography", "movies", "history");
        var set = await Sut().BuildAsync(Language.En);

        Assert.Equal(
            Enumerable.Range(0, MatchRules.QuestionsPerMatch).Select(slot => MatchRules.LevelForSlot(slot)),
            set.Select(q => q.Level));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(100)]
    public async Task Builds_a_match_of_the_requested_length(int count)
    {
        Stock(40, Language.En, "geography", "movies", "history");
        var set = await Sut().BuildAsync(Language.En, questionCount: count);

        Assert.Equal(count, set.Count);
        Assert.Equal(count, set.Select(q => q.Id).Distinct().Count());
        Assert.Equal(
            Enumerable.Range(0, count).Select(slot => MatchRules.LevelForSlot(slot, count)),
            set.Select(q => q.Level));
    }

    [Fact]
    public async Task A_named_category_is_the_only_one_used()
    {
        Stock(5, Language.En, "geography", "movies", "history");
        var set = await Sut().BuildAsync(Language.En, ["history"]);

        Assert.All(set, q => Assert.Equal("history", q.CategoryId));
    }

    [Fact]
    public async Task An_unknown_category_falls_back_to_a_random_three()
    {
        Stock(5, Language.En, "geography", "movies", "history");
        var set = await Sut().BuildAsync(Language.En, ["nonsense"]);

        Assert.Equal(MatchRules.CategoriesPerMatch, set.Select(q => q.CategoryId).Distinct().Count());
    }

    [Fact]
    public async Task Only_approved_questions_are_ever_served()
    {
        Stock(3, Language.En, "geography", "movies", "history");
        await _questions.UpsertAsync(Q("pending-1", Language.En, "geography", Difficulty.Easy, QuestionStatus.Pending));
        await _questions.UpsertAsync(Q("rejected-1", Language.En, "geography", Difficulty.Easy, QuestionStatus.Rejected));

        for (var i = 0; i < 20; i++)
        {
            var set = await Sut().BuildAsync(Language.En);
            Assert.DoesNotContain(set, q => q.Id is "pending-1" or "rejected-1");
        }
    }

    [Fact]
    public async Task Questions_are_all_in_the_requested_language()
    {
        Stock(3, Language.En, "geography", "movies", "history");
        Stock(3, Language.Fa, "geography", "movies", "history");
        var set = await Sut().BuildAsync(Language.Fa);
        Assert.All(set, q => Assert.Equal(Language.Fa, q.Lang));
    }

    [Fact]
    public async Task An_empty_bucket_falls_back_to_another_level_rather_than_failing()
    {
        // geography has only easy questions; the hard slots must still be filled.
        _categories.UpsertAsync(new Category("geography", "j", "geography", "*", "#fff"));
        _categories.UpsertAsync(new Category("movies", "f", "movies", "*", "#fff"));
        _categories.UpsertAsync(new Category("history", "t", "history", "*", "#fff"));
        for (var i = 0; i < 10; i++)
        {
            await _questions.UpsertAsync(Q($"g{i}", Language.En, "geography", Difficulty.Easy));
            await _questions.UpsertAsync(Q($"m{i}", Language.En, "movies", Difficulty.Easy));
            await _questions.UpsertAsync(Q($"h{i}", Language.En, "history", Difficulty.Easy));
        }

        var set = await Sut().BuildAsync(Language.En);
        Assert.Equal(MatchRules.QuestionsPerMatch, set.Count);
    }

    [Fact]
    public async Task Refuses_to_build_a_match_it_cannot_fill()
    {
        Stock(1, Language.En, "geography");
        await Assert.ThrowsAsync<NotEnoughQuestionsException>(() => Sut().BuildAsync(Language.En));
    }

    [Fact]
    public async Task Inactive_categories_are_not_drawn_from()
    {
        Stock(3, Language.En, "geography", "movies", "history");
        _categories.UpsertAsync(new Category("banned", "x", "banned", "*", "#fff", IsActive: false));
        foreach (var level in new[] { Difficulty.Easy, Difficulty.Medium, Difficulty.Hard })
            for (var i = 0; i < 5; i++)
                await _questions.UpsertAsync(Q($"banned-{level}-{i}", Language.En, "banned", level));

        for (var i = 0; i < 20; i++)
        {
            var set = await Sut().BuildAsync(Language.En);
            Assert.DoesNotContain(set, q => q.CategoryId == "banned");
        }
    }

    [Fact]
    public async Task Only_the_chosen_levels_are_served()
    {
        Stock(10, Language.En, "geography", "movies", "history");
        var set = await Sut().BuildAsync(Language.En, questionCount: 10, levels: [Difficulty.Hard, Difficulty.VeryHard]);

        Assert.All(set, q => Assert.Contains(q.Level, new[] { Difficulty.Hard, Difficulty.VeryHard }));
    }

    /// <summary>
    /// The point of the confined fallback: asking for very hard and getting very easy would be a
    /// worse answer than getting an error, because nothing on screen would say it happened.
    /// </summary>
    [Fact]
    public async Task An_empty_chosen_level_refuses_rather_than_substituting_an_easier_one()
    {
        _categories.UpsertAsync(new Category("geography", "geography", "Geography", "*", "#fff"));
        for (var i = 0; i < 20; i++)
            await _questions.UpsertAsync(Q($"easy-{i}", Language.En, "geography", Difficulty.VeryEasy));

        await Assert.ThrowsAsync<NotEnoughQuestionsException>(
            () => Sut().BuildAsync(Language.En, questionCount: 10, levels: [Difficulty.VeryHard]));
    }

    /// <summary>
    /// The bug this guards: a Persian profile picking the Dutch-only KNM category was handed ten
    /// Persian questions about birds and DNA, because the last fallback ignored the choice and drew
    /// from the whole bank. Asking for one category and getting another is a different duel wearing
    /// the right label, and nothing on screen says so.
    /// </summary>
    [Fact]
    public async Task A_named_category_with_nothing_in_your_language_refuses_rather_than_substituting()
    {
        Stock(10, Language.Fa, "geography", "nature", "science");
        _categories.UpsertAsync(new Category("knm", "knm", "knm", "*", "#fff"));
        foreach (var level in MatchRules.AllLevels)
            for (var i = 0; i < 10; i++)
                await _questions.UpsertAsync(Q($"nl-{level}-{i}", Language.Nl, "knm", level));

        await Assert.ThrowsAsync<NotEnoughQuestionsException>(
            () => Sut().BuildAsync(Language.Fa, ["knm"], questionCount: 10));
    }

    [Fact]
    public async Task A_named_category_still_falls_back_within_the_ones_you_named()
    {
        Stock(10, Language.En, "geography", "movies");

        // Nothing at all in "movies" at the very hardest level, so the run has to lean on geography
        // — but never on a category the player did not ask for.
        var set = await Sut().BuildAsync(Language.En, ["geography", "movies"], questionCount: 20);

        Assert.Equal(20, set.Count);
        Assert.All(set, q => Assert.Contains(q.CategoryId, new[] { "geography", "movies" }));
    }
}
