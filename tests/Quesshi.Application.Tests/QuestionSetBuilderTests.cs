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

    // ---- Kinds (issue #73) ----
    //
    // The spec's decision is "mixed into ordinary duels, and no quota": the proportion of each kind
    // in a duel falls out of the bank's own proportions rather than a rule. That makes these tests
    // the whole of the selection story — there is nothing to configure, so what has to be proved is
    // that nothing in the builder quietly excludes the two new kinds, and that it never assumes the
    // four choices a map question does not have.

    /// <summary>Fills every level of one category with questions of one kind.</summary>
    private void StockKind(int perBucket, string category, QuestionKind kind, Language lang = Language.En)
    {
        _categories.UpsertAsync(new Category(category, category, category, "*", "#fff"));
        foreach (var level in MatchRules.AllLevels)
            for (var i = 0; i < perBucket; i++)
                _questions.UpsertAsync(OfKind($"{category}-{kind}-{level}-{i}", lang, category, level, kind));
    }

    private static Question OfKind(string id, Language lang, string cat, Difficulty level, QuestionKind kind)
        => kind switch
        {
            QuestionKind.Sort => Question.Create(id, lang, cat, level, $"order {id}", ["a", "b", "c", "d"], 0, T0,
                status: QuestionStatus.Approved, kind: QuestionKind.Sort),
            QuestionKind.Map => Question.Create(id, lang, cat, level, $"find {id}", [], 0, T0,
                status: QuestionStatus.Approved, kind: QuestionKind.Map,
                target: MapTarget.Country("DE"), baseLayer: MapBaseLayer.Borders),
            _ => Q(id, lang, cat, level)
        };

    [Fact]
    public async Task A_bank_of_nothing_but_sorting_questions_still_builds_a_full_duel()
    {
        StockKind(3, "rivers", QuestionKind.Sort);

        var set = await Sut().BuildAsync(Language.En, ["rivers"]);

        Assert.Equal(MatchRules.QuestionsPerMatch, set.Count);
        Assert.All(set, q => Assert.Equal(QuestionKind.Sort, q.Kind));
    }

    /// <summary>
    /// The one kind that could have caught a hidden four-choices assumption: a map question stores
    /// no choices at all, so anything in the selection path that reached for <c>Choices[n]</c> or
    /// counted them would fail here and nowhere else.
    /// </summary>
    [Fact]
    public async Task A_bank_of_nothing_but_map_questions_still_builds_a_full_duel()
    {
        StockKind(3, "atlas", QuestionKind.Map);

        var set = await Sut().BuildAsync(Language.En, ["atlas"]);

        Assert.Equal(MatchRules.QuestionsPerMatch, set.Count);
        Assert.All(set, q => Assert.Equal(QuestionKind.Map, q.Kind));
        Assert.All(set, q => Assert.Empty(q.Choices));
    }

    /// <summary>
    /// No quota, and none needed: a bank holding all three kinds in one category hands them all to
    /// the same duel, because a sort or a map is eligible in exactly the place a choice question is.
    /// A duel drawn from a bank of one kind proves eligibility; this proves they are not merely
    /// eligible one at a time.
    /// </summary>
    [Fact]
    public async Task A_mixed_bank_puts_all_three_kinds_in_one_duel()
    {
        // Every level of one category holds one of each kind, so whichever level a slot asks for,
        // all three are on the table and only the sampler decides.
        _categories.UpsertAsync(new Category("mixed", "mixed", "mixed", "*", "#fff"));
        foreach (var level in MatchRules.AllLevels)
            foreach (var kind in new[] { QuestionKind.Choice, QuestionKind.Sort, QuestionKind.Map })
                for (var i = 0; i < 8; i++)
                    await _questions.UpsertAsync(OfKind($"mixed-{kind}-{level}-{i}", Language.En, "mixed", level, kind));

        var set = await Sut().BuildAsync(Language.En, ["mixed"], questionCount: 100);

        Assert.Equal(100, set.Count);
        Assert.Equal(3, set.Select(q => q.Kind).Distinct().Count());
    }
}
