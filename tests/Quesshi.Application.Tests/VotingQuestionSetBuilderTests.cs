using Quesshi.Application.UseCases;
using Quesshi.Domain;

namespace Quesshi.Application.Tests;

public sealed class VotingQuestionSetBuilderTests
{
    private readonly InMemoryVotingQuestions _questions = new();
    private VotingQuestionSetBuilder Sut() => new(_questions);

    private async Task AddAsync(string id, string? category)
        => await _questions.UpsertAsync(VotingQuestion.Create(id, Language.En, category, $"Prompt {id}",
            VotingAnswerSource.Fixed, ["one", "two"], DateTimeOffset.UnixEpoch,
            status: QuestionStatus.Approved));

    [Fact]
    public async Task Null_scope_samples_categorized_and_uncategorized_voting_content()
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
    public async Task Empty_scope_samples_none_and_explicit_scope_never_escapes()
    {
        await AddAsync("inside", "allowed");
        for (var i = 0; i < 10; i++) await AddAsync($"outside-{i}", "outside");

        await Assert.ThrowsAsync<NotEnoughQuestionsException>(() => Sut().BuildAsync(Language.En, new ContentScope([]), 1));
        await Assert.ThrowsAsync<NotEnoughQuestionsException>(() =>
            Sut().BuildAsync(Language.En, new ContentScope(["allowed"]), 2));
        var set = await Sut().BuildAsync(Language.En, new ContentScope(["outside", "allowed"]), 5);

        Assert.All(set, question => Assert.Contains(question.CategoryId, new[] { "outside", "allowed" }));
        Assert.Contains(set, question => question.CategoryId == "allowed");
    }

    [Fact]
    public async Task Voting_builder_never_repeats_a_question_and_keeps_the_default_count()
    {
        for (var i = 0; i < MatchRules.QuestionsPerMatch * 2; i++)
            await AddAsync($"question-{i}", "topic");

        var set = await Sut().BuildAsync(Language.En, new ContentScope(["topic"]), questionCount: 999);

        Assert.Equal(MatchRules.QuestionsPerMatch, set.Count);
        Assert.Equal(set.Count, set.Select(q => q.Id).Distinct().Count());
    }
}
