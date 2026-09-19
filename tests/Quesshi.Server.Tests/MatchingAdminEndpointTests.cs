using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Quesshi.Domain;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

[Collection(nameof(LiveClusterCollection))]
public sealed class MatchingAdminEndpointTests(LiveClusterFixture fixture) : IAsyncDisposable
{
    private readonly AdminApiTestHost _host = new(fixture.Cluster);

    private HttpClient AdminClient()
    {
        var client = _host.NewClient();
        var admin = AdminUser.Create($"matching-admin-{Guid.NewGuid():N}", "admin", "admin@example.com",
            "hash", DateTimeOffset.UtcNow);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _host.AdminTokenIssuer.Issue(admin));
        return client;
    }

    private static MatchingCategoryDto Category(string id = "friends")
        => new(id, "دوستان", "Friends", "Vrienden", "👥", "#123456", true, 1);

    private static SaveMatchingQuestionDto Question(string category = "m-friends", string? id = null,
        string source = "fixed")
        => new(id, "en", category, $"Which friend {Guid.NewGuid():N}?", source,
            source == "fixed" ? ["A", "B"] : [], "image", "/media/friend.png", "credit", "friends", "choice", "pending");

    private static async Task<string> ErrorAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;
    }

    private static async Task SeedCategoryAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/admin/matching/categories", Category());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Matching_question_crud_preserves_identity_metadata_and_media()
    {
        using var client = AdminClient();
        await SeedCategoryAsync(client);

        var create = await client.PostAsJsonAsync("/api/admin/matching/questions", Question());
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var saved = (await create.Content.ReadFromJsonAsync<MatchingQuestionDto>())!;
        Assert.Equal("m-friends", saved.MatchingCategoryId);
        Assert.Equal("/media/friend.png", saved.Media!.Url);
        Assert.Equal("credit", saved.Media.Attribution);
        Assert.Equal("friends|choice", saved.Topic);

        var editBody = Question(id: saved.Id) with { Prompt = "Edited prompt", Choices = ["X", "Y"], Status = "approved" };
        var edit = await client.PostAsJsonAsync("/api/admin/matching/questions", editBody);
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var edited = (await edit.Content.ReadFromJsonAsync<MatchingQuestionDto>())!;
        Assert.Equal(saved.Id, edited.Id);
        Assert.Equal("Edited prompt", edited.Prompt);
        Assert.Equal(saved.CreatedAt, edited.CreatedAt);
        Assert.Equal("approved", edited.Status);

        var page = await client.GetFromJsonAsync<AdminMatchingQuestionPageDto>("/api/admin/matching/questions?take=1");
        Assert.Contains(page!.Items, q => q.Id == saved.Id);
        Assert.True(page.Total >= 1);
    }

    [Fact]
    public async Task Unknown_edit_id_is_404_and_validation_returns_stable_codes()
    {
        using var client = AdminClient();
        await SeedCategoryAsync(client);

        var missing = await client.PostAsJsonAsync("/api/admin/matching/questions", Question(id: "does-not-exist"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var badSource = await client.PostAsJsonAsync("/api/admin/matching/questions", Question(source: "other"));
        Assert.Equal("bad_answer_source", await ErrorAsync(badSource));

        var tooFew = await client.PostAsJsonAsync("/api/admin/matching/questions", Question() with { Choices = ["one"] });
        Assert.Equal("too_few_choices", await ErrorAsync(tooFew));

        var participants = await client.PostAsJsonAsync("/api/admin/matching/questions",
            Question(source: "participants") with { Choices = ["not allowed"] });
        Assert.Equal("choices_not_allowed", await ErrorAsync(participants));

        var media = await client.PostAsJsonAsync("/api/admin/matching/questions",
            Question() with { MediaKind = "bogus" });
        Assert.Equal("bad_media", await ErrorAsync(media));
    }

    [Fact]
    public async Task Matching_categories_are_namespaced_and_trivia_rejects_reserved_prefix()
    {
        using var client = AdminClient();
        var category = await client.PostAsJsonAsync("/api/admin/matching/categories", Category("household"));
        Assert.Equal(HttpStatusCode.OK, category.StatusCode);

        var categories = await client.GetFromJsonAsync<List<MatchingCategoryDto>>("/api/admin/matching/categories");
        Assert.Contains(categories!, c => c.Id == "m-household");

        var trivia = await client.PostAsJsonAsync("/api/admin/categories",
            new CategoryDto("m-household", "Household", "خانه", "Household", "◆", "#123456", true, 0));
        Assert.Equal("reserved_prefix", await ErrorAsync(trivia));
    }

    [Theory]
    [InlineData("🍳")]
    [InlineData("🇳🇱")]
    [InlineData("👨‍👩‍👧‍👦")]
    [InlineData("✈️")]
    [InlineData("👍🏽")]
    public async Task Both_category_endpoints_preserve_explicit_emoji_and_emoji_prefixed_names(string icon)
    {
        using var client = AdminClient();
        var suffix = Guid.NewGuid().ToString("N");

        var matching = await client.PostAsJsonAsync("/api/admin/matching/categories",
            Category("emoji-" + suffix) with { Icon = $" {icon} ", NameFa = "🍳 فارسی", NameEn = "🍳 English", NameNl = "🍳 Dutch" });
        Assert.Equal(HttpStatusCode.OK, matching.StatusCode);
        var matchingSaved = (await matching.Content.ReadFromJsonAsync<MatchingCategoryDto>())!;
        Assert.Equal(icon, matchingSaved.Icon);
        Assert.Equal("🍳 فارسی", matchingSaved.NameFa);
        Assert.Equal("🍳 English", matchingSaved.NameEn);
        Assert.Equal("🍳 Dutch", matchingSaved.NameNl);

        var triviaId = "emoji-" + suffix;
        var trivia = await client.PostAsJsonAsync("/api/admin/categories",
            new CategoryDto(triviaId, "display", "🍳 فارسی", "🍳 English", $" {icon} ", "#123456", true, 1, "🍳 Dutch"));
        Assert.Equal(HttpStatusCode.OK, trivia.StatusCode);
        var triviaRows = await client.GetFromJsonAsync<List<CategoryDto>>("/api/admin/categories");
        var triviaSaved = triviaRows!.Single(c => c.Id == triviaId);
        Assert.Equal(icon, triviaSaved.Icon);
        Assert.Equal("🍳 فارسی", triviaSaved.NameFa);
        Assert.Equal("🍳 English", triviaSaved.NameEn);
        Assert.Equal("🍳 Dutch", triviaSaved.NameNl);
    }

    [Fact]
    public async Task Trivia_category_prompt_helper_is_trimmed_and_returned_after_save()
    {
        using var client = AdminClient();
        var id = "prompt-helper-" + Guid.NewGuid().ToString("N");

        var response = await client.PostAsJsonAsync("/api/admin/categories",
            new CategoryDto(id, "History", "تاریخ", "History", "📜", "#123456", true, 1,
                PromptHelper: "  Focus on overlooked events.  "));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var categories = await client.GetFromJsonAsync<List<CategoryDto>>("/api/admin/categories");
        Assert.Equal("Focus on overlooked events.", categories!.Single(c => c.Id == id).PromptHelper);
    }

    [Theory]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("◆")]
    [InlineData("😀😃")]
    [InlineData("🏽")]
    [InlineData("👨‍")]
    public async Task Both_category_endpoints_reject_invalid_icons_without_writing(string icon)
    {
        using var client = AdminClient();
        var suffix = Guid.NewGuid().ToString("N");

        var matching = await client.PostAsJsonAsync("/api/admin/matching/categories",
            Category("bad-icon-" + suffix) with { Icon = icon });
        Assert.Equal("bad_icon", await ErrorAsync(matching));

        var trivia = await client.PostAsJsonAsync("/api/admin/categories",
            new CategoryDto("bad-icon-" + suffix, "display", "فارسی", "English", icon, "#123456", true, 1, "Dutch"));
        Assert.Equal("bad_icon", await ErrorAsync(trivia));
    }

    [Fact]
    public async Task Matching_question_delete_does_not_delete_trivia_question_with_same_id()
    {
        using var client = AdminClient();
        await SeedCategoryAsync(client);
        var id = $"shared-{Guid.NewGuid():N}";
        var triviaCategory = "matching-admin-trivia";
        if (LiveShared.Categories.Items.All(c => c.Id != triviaCategory))
            LiveShared.Categories.Items.Add(new Category(triviaCategory, "تریویا", "Trivia", "◆", "#123456"));
        LiveShared.Questions.Items.Add(Quesshi.Domain.Question.Create(id, Language.En, triviaCategory, Difficulty.Easy,
            "Trivia", ["a", "b", "c", "d"], 0, DateTimeOffset.UtcNow));

        var response = await client.DeleteAsync($"/api/admin/matching/questions/{id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotNull(await LiveShared.Questions.GetAsync(id));
    }

    [Fact]
    public async Task Matching_generation_has_its_own_validated_admin_route()
    {
        using var client = AdminClient();
        await SeedCategoryAsync(client);

        var response = await client.PostAsJsonAsync("/api/admin/matching/generate",
            new GenerateMatchingRequestDto("en", "friends", "participants", 5));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = (await response.Content.ReadFromJsonAsync<MatchingGenerationRunDto>())!;
        Assert.Equal("m-friends", run.CategoryId);
        Assert.Equal("participants", run.AnswerSource);
        Assert.Equal("generator_not_configured", run.Error);
        Assert.Equal(0, run.Requested);

        var badLang = await client.PostAsJsonAsync("/api/admin/matching/generate",
            new GenerateMatchingRequestDto("de", "friends", "participants", 5));
        Assert.Equal("bad_lang", await ErrorAsync(badLang));

        var badSource = await client.PostAsJsonAsync("/api/admin/matching/generate",
            new GenerateMatchingRequestDto("en", "friends", "correct", 5));
        Assert.Equal("bad_answer_source", await ErrorAsync(badSource));

        var badCount = await client.PostAsJsonAsync("/api/admin/matching/generate",
            new GenerateMatchingRequestDto("en", "friends", "fixed", 21));
        Assert.Equal("bad_count", await ErrorAsync(badCount));

        var missingCategory = await client.PostAsJsonAsync("/api/admin/matching/generate",
            new GenerateMatchingRequestDto("en", "missing", "fixed", 5));
        Assert.Equal("unknown_category", await ErrorAsync(missingCategory));
    }

    [Fact]
    public async Task Trivia_generation_endpoint_forwards_the_additional_prompt()
    {
        using var client = AdminClient();
        var categoryId = "generation-prompt-" + Guid.NewGuid().ToString("N");
        await LiveShared.Categories.UpsertAsync(new Category(categoryId, "تاریخ", "History", "📜", "#123456"));
        _host.QuestionGenerator.AdditionalPrompts.Clear();
        _host.QuestionGenerator.IsConfigured = true;

        var response = await client.PostAsJsonAsync("/api/admin/generate/bucket",
            new GenerateRequestDto("en", categoryId, 2, 1, AdditionalPrompt: "Focus on the Silk Road."));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Focus on the Silk Road.", Assert.Single(_host.QuestionGenerator.AdditionalPrompts));
    }

    [Theory]
    [InlineData("/api/admin/matching/questions")]
    [InlineData("/api/admin/matching/categories")]
    public async Task Matching_routes_require_admin_authorization(string path)
    {
        using var client = _host.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Matching_generation_requires_admin_authorization()
    {
        using var client = _host.NewClient();
        var response = await client.PostAsJsonAsync("/api/admin/matching/generate",
            new GenerateMatchingRequestDto("en", "m-friends", "participants", 5));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();
}
