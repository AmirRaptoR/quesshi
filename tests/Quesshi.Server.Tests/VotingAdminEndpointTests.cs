using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Quesshi.Domain;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

[Collection(nameof(LiveClusterCollection))]
public sealed class VotingAdminEndpointTests(LiveClusterFixture fixture) : IAsyncDisposable
{
    private readonly AdminApiTestHost _host = new(fixture.Cluster);

    private HttpClient AdminClient()
    {
        var client = _host.NewClient();
        var admin = AdminUser.Create($"voting-admin-{Guid.NewGuid():N}", "admin", "admin@example.com",
            "hash", DateTimeOffset.UtcNow);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _host.AdminTokenIssuer.Issue(admin));
        return client;
    }

    private static CategoryDto Category(string id = "friends")
        => new(id, "دوستان", "Friends", "Vrienden", "👥", "#123456", true, 1);

    private static SaveVotingQuestionDto Question(string category = "friends", string? id = null,
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
        var response = await client.PostAsJsonAsync("/api/admin/categories", Category());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Voting_question_crud_preserves_identity_metadata_and_media()
    {
        using var client = AdminClient();
        await SeedCategoryAsync(client);

        var create = await client.PostAsJsonAsync("/api/admin/voting/questions", Question());
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var saved = (await create.Content.ReadFromJsonAsync<VotingQuestionDto>())!;
        Assert.Equal("friends", saved.CategoryId);
        Assert.Equal("/media/friend.png", saved.Media!.Url);
        Assert.Equal("credit", saved.Media.Attribution);
        Assert.Equal("friends|choice", saved.Topic);

        var editBody = Question(id: saved.Id) with { Prompt = "Edited prompt", Choices = ["X", "Y"], Status = "approved" };
        var edit = await client.PostAsJsonAsync("/api/admin/voting/questions", editBody);
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var edited = (await edit.Content.ReadFromJsonAsync<VotingQuestionDto>())!;
        Assert.Equal(saved.Id, edited.Id);
        Assert.Equal("Edited prompt", edited.Prompt);
        Assert.Equal(saved.CreatedAt, edited.CreatedAt);
        Assert.Equal("approved", edited.Status);

        var page = await client.GetFromJsonAsync<AdminVotingQuestionPageDto>("/api/admin/voting/questions?take=1");
        Assert.Contains(page!.Items, q => q.Id == saved.Id);
        Assert.True(page.Total >= 1);
    }

    [Fact]
    public async Task Unknown_edit_id_is_404_and_validation_returns_stable_codes()
    {
        using var client = AdminClient();
        await SeedCategoryAsync(client);

        var missing = await client.PostAsJsonAsync("/api/admin/voting/questions", Question(id: "does-not-exist"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var badSource = await client.PostAsJsonAsync("/api/admin/voting/questions", Question(source: "other"));
        Assert.Equal("bad_answer_source", await ErrorAsync(badSource));

        var tooFew = await client.PostAsJsonAsync("/api/admin/voting/questions", Question() with { Choices = ["one"] });
        Assert.Equal("too_few_choices", await ErrorAsync(tooFew));

        var participants = await client.PostAsJsonAsync("/api/admin/voting/questions",
            Question(source: "participants") with { Choices = ["not allowed"] });
        Assert.Equal("choices_not_allowed", await ErrorAsync(participants));

        var media = await client.PostAsJsonAsync("/api/admin/voting/questions",
            Question() with { MediaKind = "bogus" });
        Assert.Equal("bad_media", await ErrorAsync(media));
    }

    [Fact]
    public async Task Shared_category_can_be_configured_for_both_game_families()
    {
        using var client = AdminClient();
        var category = await client.PostAsJsonAsync("/api/admin/categories", Category("household"));
        Assert.Equal(HttpStatusCode.OK, category.StatusCode);

        var saved = await client.PutAsJsonAsync("/api/admin/content-settings",
            new ContentSettingsDto(["household"], ["household"]));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var settings = await saved.Content.ReadFromJsonAsync<ContentSettingsDto>();
        Assert.Equal(["household"], settings!.TriviaCategoryIds);
        Assert.Equal(["household"], settings.VotingCategoryIds);

        var reloaded = await client.GetFromJsonAsync<Quesshi.Shared.AdminContentSettingsDto>("/api/admin/content-settings");
        Assert.Equal(["household"], reloaded!.Settings.TriviaCategoryIds);
        Assert.Equal(["household"], reloaded.Settings.VotingCategoryIds);
    }

    [Theory]
    [InlineData("🍳")]
    [InlineData("🇳🇱")]
    [InlineData("👨‍👩‍👧‍👦")]
    [InlineData("✈️")]
    [InlineData("👍🏽")]
    public async Task Shared_category_endpoint_preserves_explicit_emoji_and_emoji_prefixed_names(string icon)
    {
        using var client = AdminClient();
        var suffix = Guid.NewGuid().ToString("N");

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

    [Theory]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("◆")]
    [InlineData("😀😃")]
    [InlineData("🏽")]
    [InlineData("👨‍")]
    public async Task Shared_category_endpoint_rejects_invalid_icons_without_writing(string icon)
    {
        using var client = AdminClient();
        var suffix = Guid.NewGuid().ToString("N");

        var trivia = await client.PostAsJsonAsync("/api/admin/categories",
            new CategoryDto("bad-icon-" + suffix, "display", "فارسی", "English", icon, "#123456", true, 1, "Dutch"));
        Assert.Equal("bad_icon", await ErrorAsync(trivia));
    }

    [Fact]
    public async Task Voting_question_delete_does_not_delete_trivia_question_with_same_id()
    {
        using var client = AdminClient();
        await SeedCategoryAsync(client);
        var id = $"shared-{Guid.NewGuid():N}";
        var triviaCategory = "voting-admin-trivia";
        if (LiveShared.Categories.Items.All(c => c.Id != triviaCategory))
            LiveShared.Categories.Items.Add(new Category(triviaCategory, "تریویا", "Trivia", "◆", "#123456"));
        LiveShared.Questions.Items.Add(Quesshi.Domain.Question.Create(id, Language.En, triviaCategory, Difficulty.Easy,
            "Trivia", ["a", "b", "c", "d"], 0, DateTimeOffset.UtcNow));

        var response = await client.DeleteAsync($"/api/admin/voting/questions/{id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotNull(await LiveShared.Questions.GetAsync(id));
    }

    [Fact]
    public async Task Voting_generation_has_its_own_validated_admin_route()
    {
        using var client = AdminClient();
        await SeedCategoryAsync(client);

        var response = await client.PostAsJsonAsync("/api/admin/voting/generate",
            new GenerateVotingRequestDto("en", "friends", "participants", 5));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = (await response.Content.ReadFromJsonAsync<VotingGenerationRunDto>())!;
        Assert.Equal("friends", run.CategoryId);
        Assert.Equal("participants", run.AnswerSource);
        Assert.Equal("generator_not_configured", run.Error);
        Assert.Equal(0, run.Requested);

        var badLang = await client.PostAsJsonAsync("/api/admin/voting/generate",
            new GenerateVotingRequestDto("de", "friends", "participants", 5));
        Assert.Equal("bad_lang", await ErrorAsync(badLang));

        var badSource = await client.PostAsJsonAsync("/api/admin/voting/generate",
            new GenerateVotingRequestDto("en", "friends", "correct", 5));
        Assert.Equal("bad_answer_source", await ErrorAsync(badSource));

        var badCount = await client.PostAsJsonAsync("/api/admin/voting/generate",
            new GenerateVotingRequestDto("en", "friends", "fixed", 21));
        Assert.Equal("bad_count", await ErrorAsync(badCount));

        var missingCategory = await client.PostAsJsonAsync("/api/admin/voting/generate",
            new GenerateVotingRequestDto("en", "missing", "fixed", 5));
        Assert.Equal("unknown_category", await ErrorAsync(missingCategory));
    }

    [Theory]
    [InlineData("/api/admin/voting/questions")]
    [InlineData("/api/admin/categories")]
    [InlineData("/api/admin/content-settings")]
    public async Task Voting_routes_require_admin_authorization(string path)
    {
        using var client = _host.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Content_settings_cannot_be_changed_without_admin_authorization()
    {
        using var client = _host.NewClient();
        var response = await client.PutAsJsonAsync("/api/admin/content-settings", new ContentSettingsDto(["trivia"], ["voting"]));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Voting_generation_requires_admin_authorization()
    {
        using var client = _host.NewClient();
        var response = await client.PostAsJsonAsync("/api/admin/voting/generate",
            new GenerateVotingRequestDto("en", "friends", "participants", 5));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Shared_category_deletion_checks_voting_questions_too()
    {
        using var client = AdminClient();
        const string categoryId = "shared-voting-use";
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/api/admin/categories", Category(categoryId))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/api/admin/voting/questions", Question(categoryId))).StatusCode);

        var deletion = await client.DeleteAsync($"/api/admin/categories/{categoryId}");

        Assert.Equal(HttpStatusCode.BadRequest, deletion.StatusCode);
        var categories = await client.GetFromJsonAsync<List<CategoryDto>>("/api/admin/categories");
        Assert.Contains(categories!, category => category.Id == categoryId && category.IsActive);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();
}
