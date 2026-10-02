using Quesshi.Application.UseCases;
using Quesshi.Domain;

namespace Quesshi.Application.Tests;

public sealed class QuestionSetBuilderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private readonly InMemoryQuestions _questions = new();
    private QuestionSetBuilder Sut() => new(_questions);

    private async Task AddAsync(string id, string? category, Difficulty level = Difficulty.Easy)
        => await _questions.UpsertAsync(Question.Create(id, Language.En, category, level, $"Prompt {id}",
            ["a", "b", "c", "d"], 0, Now, status: QuestionStatus.Approved));

    [Fact]
    public async Task Null_scope_samples_categorized_and_uncategorized_content()
    {
        for (var i = 0; i < MatchRules.QuestionsPerMatch / 2; i++)
        {
            await AddAsync($"uncategorized-{i}", null);
            await AddAsync($"categorized-{i}", "topic");
        }

        var set = await Sut().BuildAsync(Language.En, ContentScope.All);

        Assert.Equal(MatchRules.QuestionsPerMatch, set.Count);
        Assert.Contains(set, question => question.CategoryId is null);
        Assert.Contains(set, question => question.CategoryId == "topic");
    }

    [Fact]
    public async Task Empty_scope_samples_none()
    {
        await AddAsync("q", "topic");

        await Assert.ThrowsAsync<NotEnoughQuestionsException>(() =>
            Sut().BuildAsync(Language.En, new ContentScope([]), questionCount: 1));
    }

    [Fact]
    public async Task Explicit_scope_never_falls_back_outside_supplied_categories()
    {
        await AddAsync("inside", "allowed");
        for (var i = 0; i < 10; i++) await AddAsync($"outside-{i}", "outside");

        await Assert.ThrowsAsync<NotEnoughQuestionsException>(() =>
            Sut().BuildAsync(Language.En, new ContentScope(["allowed"]), questionCount: 2));
        var set = await Sut().BuildAsync(Language.En, new ContentScope(["outside", "allowed"]), questionCount: 5);

        Assert.All(set, question => Assert.Contains(question.CategoryId, new[] { "outside", "allowed" }));
        Assert.Contains(set, question => question.CategoryId == "allowed");
    }

    [Fact]
    public async Task Scoped_draws_spread_categories_and_keep_the_difficulty_ramp()
    {
        foreach (var category in new[] { "a", "b" })
            foreach (var level in MatchRules.AllLevels)
                for (var i = 0; i < 3; i++) await AddAsync($"{category}-{level}-{i}", category, level);

        var set = await Sut().BuildAsync(Language.En, new ContentScope(["a", "b"]),
            MatchRules.QuestionsPerMatch);

        Assert.Contains(set, question => question.CategoryId == "a");
        Assert.Contains(set, question => question.CategoryId == "b");
        Assert.Equal(Enumerable.Range(0, set.Count)
            .Select(slot => MatchRules.LevelForSlot(slot, set.Count, MatchRules.AllLevels)), set.Select(q => q.Level));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(100)]
    public async Task Builds_the_requested_length_without_repeats(int count)
    {
        foreach (var category in new[] { "a", "b", "c" })
            foreach (var level in MatchRules.AllLevels)
                for (var i = 0; i < count; i++) await AddAsync($"{category}-{level}-{i}", category, level);

        var set = await Sut().BuildAsync(Language.En, new ContentScope(["a", "b", "c"]), count);

        Assert.Equal(count, set.Count);
        Assert.Equal(count, set.Select(q => q.Id).Distinct().Count());
        Assert.Equal(Enumerable.Range(0, count).Select(slot => MatchRules.LevelForSlot(slot, count, MatchRules.AllLevels)),
            set.Select(q => q.Level));
    }

    [Fact]
    public async Task Samples_only_approved_questions_in_the_requested_language()
    {
        for (var i = 0; i < MatchRules.QuestionsPerMatch; i++)
        {
            await AddAsync($"en-{i}", "topic");
            await _questions.UpsertAsync(Question.Create($"fa-{i}", Language.Fa, "topic", MatchRules.LevelForSlot(i),
                $"Prompt fa-{i}", ["a", "b", "c", "d"], 0, Now, status: QuestionStatus.Approved));
        }
        await _questions.UpsertAsync(Question.Create("pending", Language.En, "topic", Difficulty.Easy,
            "Pending", ["a", "b", "c", "d"], 0, Now, status: QuestionStatus.Pending));

        var set = await Sut().BuildAsync(Language.Fa, new ContentScope(["topic"]));

        Assert.All(set, q => Assert.Equal(Language.Fa, q.Lang));
        Assert.DoesNotContain(set, q => q.Status != QuestionStatus.Approved);
    }

    [Fact]
    public async Task Empty_difficulty_buckets_fall_back_within_scope_but_never_outside_it()
    {
        for (var i = 0; i < MatchRules.QuestionsPerMatch; i++)
            await AddAsync($"inside-{i}", "inside", Difficulty.Easy);
        for (var i = 0; i < MatchRules.QuestionsPerMatch * 2; i++)
            await AddAsync($"outside-{i}", "outside", Difficulty.Easy);

        var set = await Sut().BuildAsync(Language.En, new ContentScope(["inside"]));

        Assert.All(set, q => Assert.Equal("inside", q.CategoryId));
    }

    [Fact]
    public async Task Refuses_unavailable_levels_instead_of_substituting_outside_the_requested_levels()
    {
        for (var i = 0; i < MatchRules.QuestionsPerMatch; i++)
            await AddAsync($"easy-{i}", "topic", Difficulty.VeryEasy);

        await Assert.ThrowsAsync<NotEnoughQuestionsException>(() =>
            Sut().BuildAsync(Language.En, new ContentScope(["topic"]), levels: [Difficulty.VeryHard]));
    }

    [Theory]
    [InlineData(QuestionKind.Sort)]
    [InlineData(QuestionKind.Map)]
    public async Task Set_builder_accepts_supported_question_kinds_without_choice_assumptions(QuestionKind kind)
    {
        for (var levelIndex = 0; levelIndex < MatchRules.AllLevels.Length; levelIndex++)
            for (var i = 0; i < 3; i++)
            {
                var level = MatchRules.AllLevels[levelIndex];
                var question = kind == QuestionKind.Sort
                    ? Question.Create($"sort-{level}-{i}", Language.En, "topic", level, "Order these", ["a", "b", "c", "d"], 0,
                        Now, status: QuestionStatus.Approved, kind: QuestionKind.Sort)
                    : Question.Create($"map-{level}-{i}", Language.En, "topic", level, "Find the country", [], 0,
                        Now, status: QuestionStatus.Approved, kind: QuestionKind.Map,
                        target: MapTarget.Country("DE"), baseLayer: MapBaseLayer.Borders);
                await _questions.UpsertAsync(question);
            }

        var set = await Sut().BuildAsync(Language.En, new ContentScope(["topic"]));

        Assert.All(set, q => Assert.Equal(kind, q.Kind));
        if (kind == QuestionKind.Map) Assert.All(set, q => Assert.Empty(q.Choices));
    }
}
