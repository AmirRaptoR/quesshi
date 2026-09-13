using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Quesshi.Domain;
using Quesshi.Server.Api;

namespace Quesshi.Server.Tests;

public sealed class MatchingCategoryDeletionTests
{
    [Fact]
    public async Task Empty_category_is_retired_so_a_concurrent_save_cannot_create_an_orphan()
    {
        var categories = new FakeMatchingCategories();
        categories.Items.Add(new MatchingCategory("m-empty", "فارسی", "Empty", "◆", "#123456"));
        var questions = new FakeMatchingQuestions();

        var result = await MatchingAdminEndpoints.DeleteCategoryAsync("empty", categories, questions);

        Assert.IsType<Ok>(result);
        var retired = Assert.Single(categories.Items);
        Assert.Equal("m-empty", retired.Id);
        Assert.False(retired.IsActive);
        Assert.Empty(questions.Items);
    }

    [Fact]
    public async Task Category_with_questions_keeps_the_category_in_use_contract()
    {
        var categories = new FakeMatchingCategories();
        categories.Items.Add(new MatchingCategory("m-used", "فارسی", "Used", "◆", "#123456"));
        var questions = new FakeMatchingQuestions();
        await questions.UpsertAsync(MatchingQuestion.Create("q", Language.En, "m-used", "Prompt",
            MatchingAnswerSource.Participants, [], DateTimeOffset.UnixEpoch));

        var result = await MatchingAdminEndpoints.DeleteCategoryAsync("m-used", categories, questions);

        var badRequest = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, badRequest.StatusCode);
        Assert.Equal("m-used", Assert.Single(categories.Items).Id);
        Assert.True(Assert.Single(categories.Items).IsActive);
    }
}
