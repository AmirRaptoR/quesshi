using Quesshi.Application.UseCases;
using Quesshi.Domain;

namespace Quesshi.Application.Tests;

public sealed class MatchingQuestionSetBuilderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
    private readonly InMemoryMatchingQuestions _questions = new();
    private readonly InMemoryMatchingCategories _categories = new();

    private MatchingQuestionSetBuilder Sut() => new(_questions, _categories);

    private async Task Stock(int perCategory, params string[] ids)
    {
        foreach (var id in ids)
        {
            await _categories.UpsertAsync(new MatchingCategory(id, id, id, "*", "#fff"));
            for (var i = 0; i < perCategory; i++)
                await _questions.UpsertAsync(MatchingQuestion.Create($"{id}-{i}", Language.En, id,
                    $"prompt {id}-{i}", MatchingAnswerSource.Fixed, ["one", "two"], T0,
                    status: QuestionStatus.Approved));
        }
    }

    [Fact]
    public async Task Draws_only_matching_questions_and_never_repeats()
    {
        await Stock(10, "a", "b", "c");

        var result = await Sut().BuildAsync(Language.En, ["a", "b", "c"], 10);

        Assert.Equal(10, result.Count);
        Assert.Equal(10, result.Select(q => q.Id).Distinct().Count());
        Assert.All(result, q => Assert.IsType<MatchingQuestion>(q));
    }

    [Fact]
    public async Task Named_categories_are_exclusive_and_rotate_in_order()
    {
        await Stock(10, "a", "b", "c");

        var result = await Sut().BuildAsync(Language.En, ["b", "a"], 10);

        Assert.Equal(["b", "a", "b", "a", "b", "a", "b", "a", "b", "a"],
            result.Select(q => q.MatchingCategoryId));
    }

    [Fact]
    public async Task Unknown_or_inactive_named_ids_are_dropped_and_all_dropped_means_random_active_set()
    {
        await Stock(10, "a", "b", "c", "inactive");
        await _categories.UpsertAsync(new MatchingCategory("inactive", "", "", "", "", false));

        var result = await Sut().BuildAsync(Language.En, ["missing", "inactive", "inactive"], 10);

        Assert.Equal(3, result.Select(q => q.MatchingCategoryId).Distinct().Count());
        Assert.DoesNotContain("inactive", result.Select(q => q.MatchingCategoryId));
    }

    [Fact]
    public async Task Named_shortfall_does_not_escape_to_an_unnamed_category()
    {
        await Stock(10, "a", "b");
        await _categories.UpsertAsync(new MatchingCategory("empty", "", "", "", ""));

        var error = await Assert.ThrowsAsync<NotEnoughQuestionsException>(() =>
            Sut().BuildAsync(Language.En, ["empty"], 10));

        Assert.Contains("0 of 10", error.Message);
        Assert.Contains("empty", error.Message);
    }

    [Fact]
    public async Task Invalid_builder_count_uses_the_default()
    {
        await Stock(10, "a", "b", "c");

        var result = await Sut().BuildAsync(Language.En, ["a", "b", "c"], 7);

        Assert.Equal(MatchRules.QuestionsPerMatch, result.Count);
    }

    [Fact]
    public async Task No_active_matching_categories_is_a_not_enough_error()
    {
        await _categories.UpsertAsync(new MatchingCategory("off", "", "", "", "", false));

        var error = await Assert.ThrowsAsync<NotEnoughQuestionsException>(() => Sut().BuildAsync(Language.En));

        Assert.Contains("no active categories", error.Message);
    }
}
