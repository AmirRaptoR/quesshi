using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Quesshi.Domain;
using Quesshi.Server.Api;

namespace Quesshi.Server.Tests;

public sealed class VotingCategoryDeletionTests
{
    [Fact]
    public async Task Empty_category_is_retired_so_a_concurrent_save_cannot_create_an_orphan()
    {
        var categories = new FakeVotingCategories();
        categories.Items.Add(new VotingCategory("m-empty", "فارسی", "Empty", "◆", "#123456"));
        var questions = new FakeVotingQuestions();

        var result = await VotingAdminEndpoints.DeleteCategoryAsync("empty", categories, questions);

        Assert.IsType<Ok>(result);
        var retired = Assert.Single(categories.Items);
        Assert.Equal("m-empty", retired.Id);
        Assert.False(retired.IsActive);
        Assert.Empty(questions.Items);
    }

    [Fact]
    public async Task Category_with_questions_keeps_the_category_in_use_contract()
    {
        var categories = new FakeVotingCategories();
        categories.Items.Add(new VotingCategory("m-used", "فارسی", "Used", "◆", "#123456"));
        var questions = new FakeVotingQuestions();
        await questions.UpsertAsync(VotingQuestion.Create("q", Language.En, "m-used", "Prompt",
            VotingAnswerSource.Participants, [], DateTimeOffset.UnixEpoch));

        var result = await VotingAdminEndpoints.DeleteCategoryAsync("m-used", categories, questions);

        var badRequest = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, badRequest.StatusCode);
        Assert.Equal("m-used", Assert.Single(categories.Items).Id);
        Assert.True(Assert.Single(categories.Items).IsActive);
    }
}
