using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quesshi.Infrastructure.Database;
using Quesshi.Application.Media;
using Quesshi.Infrastructure.Security;
using Quesshi.Server.Security;
using Quesshi.Testing;
using Xunit.Sdk;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class AdminQuestionDatabaseTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly Guid TenantA = Guid.Parse("8ab4e15b-45e9-4d67-a32b-b80cbe902c3a");
    private static readonly Guid TenantB = Guid.Parse("54e5eb40-2d22-48a2-9f36-e967f67a1c44");

    [SkippableFact]
    public async Task Api_editor_can_create_a_choice_question_and_read_its_payload()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "history");
        var editor = await InsertAdminAsync(owner, "question-editor", "question-editor@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        using var body = JsonContent.Create(ChoiceRequest("Which city is the capital of France?", category));
        using var created = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookie, token, body);
        var question = await created.Content.ReadFromJsonAsync<QuestionResponse>();
        using var fetched = await SendAsync(client, HttpMethod.Get, $"/api/admin/questions/{question!.Id}", cookie);
        var reread = await fetched.Content.ReadFromJsonAsync<QuestionResponse>();

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.True(JsonElement.DeepEquals(question.Payload, reread!.Payload));
    }

    [SkippableFact]
    public async Task Api_import_defaults_to_a_dry_run_without_creating_questions()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "import-dry-run");
        var editor = await InsertAdminAsync(owner, "import-dry-run", "import-dry-run@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        using var body = ImportBody(category, "Dry run question");
        using var response = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, body);
        await using var count = new NpgsqlCommand("select count(*) from questions where tenant_id = @tenant", owner);
        count.Parameters.AddWithValue("tenant", TenantA);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
    }

    [SkippableFact]
    public async Task Api_import_commits_row_status_with_import_source()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "import-commit");
        var editor = await InsertAdminAsync(owner, "import-commit", "import-commit@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        using var body = ImportBody(category, "Committed import question", dryRun: false, status: "approved");
        using var response = await SendAsync(client, HttpMethod.Post, "/api/admin/import", cookie, token, body);
        var report = await response.Content.ReadAsStringAsync();
        await using var stored = new NpgsqlCommand("select status || ':' || source from questions where tenant_id = @tenant", owner);
        stored.Parameters.AddWithValue("tenant", TenantA);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((string)(await stored.ExecuteScalarAsync())! == "approved:import", report);
    }

    [SkippableFact]
    public async Task Api_editor_uploads_tenant_media_with_attribution_and_public_delivery_is_isolated()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var editor = await InsertAdminAsync(owner, "media-editor", "media-editor@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        MediaResponse? asset = null;

        try
        {
            using var uploadBody = UploadBody(Png(), "image/png", "Photo credit");
            using var uploaded = await SendAsync(client, HttpMethod.Post, "/api/admin/media", cookie, token, uploadBody);
            asset = await uploaded.Content.ReadFromJsonAsync<MediaResponse>();
            using var delivered = await SendAsync(client, HttpMethod.Get, $"/media/{asset!.Hash}");
            using var foreign = await SendAsync(client, HttpMethod.Get, $"/media/{asset.Hash}", host: "questions-b.example.test");

            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
            Assert.Equal("Photo credit", asset.Attribution);
            Assert.Equal("image/png", delivered.Content.Headers.ContentType!.MediaType);
            Assert.Equal("public, max-age=31536000, immutable", delivered.Headers.CacheControl!.ToString());
            Assert.Equal(Png(), await delivered.Content.ReadAsByteArrayAsync());
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        }
        finally
        {
            if (asset is not null)
                await app.Services.GetRequiredService<IMediaStore>().DeleteAsync(TenantA, asset.Id);
        }
    }

    [SkippableFact]
    public async Task Api_media_upload_requires_editor_and_antiforgery_and_audio_supports_ranges()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var editor = await InsertAdminAsync(owner, "range-editor", "range-editor@example.test");
        var moderator = await InsertAdminAsync(owner, "range-moderator", "range-moderator@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await MembershipAsync(owner, TenantA, moderator, "moderator");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var editorCookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var moderatorCookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, moderator);
        var editorToken = await AdminAntiforgeryAsync(client, editorCookie);
        var moderatorToken = await AdminAntiforgeryAsync(client, moderatorCookie);
        MediaResponse? asset = null;

        try
        {
            using var moderatorBody = UploadBody(Wav(), "audio/wav");
            using var moderatorResponse = await SendAsync(client, HttpMethod.Post, "/api/admin/media", moderatorCookie, moderatorToken, moderatorBody);
            using var anonymousBody = UploadBody(Wav(), "audio/wav");
            using var anonymousResponse = await SendAsync(client, HttpMethod.Post, "/api/admin/media", content: anonymousBody);
            using var missingTokenBody = UploadBody(Wav(), "audio/wav");
            using var missingTokenResponse = await SendAsync(client, HttpMethod.Post, "/api/admin/media", editorCookie, content: missingTokenBody);
            using var validBody = UploadBody(Wav(), "audio/wav");
            using var uploaded = await SendAsync(client, HttpMethod.Post, "/api/admin/media", editorCookie, editorToken, validBody);
            asset = await uploaded.Content.ReadFromJsonAsync<MediaResponse>();

            using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, $"/media/{asset!.Hash}");
            rangeRequest.Headers.Host = "questions-a.example.test";
            rangeRequest.Headers.Range = new RangeHeaderValue(0, 7);
            using var range = await client.SendAsync(rangeRequest);
            using var unsatisfiableRequest = new HttpRequestMessage(HttpMethod.Get, $"/media/{asset.Hash}");
            unsatisfiableRequest.Headers.Host = "questions-a.example.test";
            unsatisfiableRequest.Headers.Range = new RangeHeaderValue(10_000, null);
            using var unsatisfiable = await client.SendAsync(unsatisfiableRequest);

            Assert.Equal(HttpStatusCode.Forbidden, moderatorResponse.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, missingTokenResponse.StatusCode);
            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
            Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
            Assert.Equal("bytes 0-7/48", range.Content.Headers.ContentRange!.ToString());
            Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, unsatisfiable.StatusCode);
        }
        finally
        {
            if (asset is not null)
                await app.Services.GetRequiredService<IMediaStore>().DeleteAsync(TenantA, asset.Id);
        }
    }

    [SkippableFact]
    public async Task Api_media_upload_rejects_size_limits_and_type_mismatches_without_assets()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var editor = await InsertAdminAsync(owner, "limits-editor", "limits-editor@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        var root = Path.Combine(Path.GetTempPath(), "quesshi-media-upload-rejections", Guid.NewGuid().ToString("N"));
        var priorRoot = Environment.GetEnvironmentVariable("MEDIA_DIR");
        Environment.SetEnvironmentVariable("MEDIA_DIR", root);
        try
        {
            await using var sources = Sources();
            await using var app = await StartAsync(sources);
            using var client = Client();
            var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
            var token = await AdminAntiforgeryAsync(client, cookie);

            using var imageOverLimit = UploadBody(new byte[5 * 1024 * 1024 + 1], "image/png");
            using var imageResponse = await SendAsync(client, HttpMethod.Post, "/api/admin/media", cookie, token, imageOverLimit);
            using var audioOverLimit = UploadBody(new byte[10 * 1024 * 1024 + 1], "audio/wav");
            using var audioResponse = await SendAsync(client, HttpMethod.Post, "/api/admin/media", cookie, token, audioOverLimit);
            using var videoOverLimit = UploadBody(new byte[25 * 1024 * 1024 + 1], "video/mp4");
            using var videoResponse = await SendAsync(client, HttpMethod.Post, "/api/admin/media", cookie, token, videoOverLimit);
            using var mismatch = UploadBody(Png(), "image/jpeg");
            using var mismatchResponse = await SendAsync(client, HttpMethod.Post, "/api/admin/media", cookie, token, mismatch);
            using var unsupported = UploadBody(Png(), "image/gif");
            using var unsupportedResponse = await SendAsync(client, HttpMethod.Post, "/api/admin/media", cookie, token, unsupported);
            using var unrecognised = UploadBody("not a media file"u8.ToArray(), "image/png");
            using var unrecognisedResponse = await SendAsync(client, HttpMethod.Post, "/api/admin/media", cookie, token, unrecognised);
            await using var count = new NpgsqlCommand("select count(*) from media_assets where tenant_id = @tenant", owner);
            count.Parameters.AddWithValue("tenant", TenantA);

            Assert.Equal(HttpStatusCode.BadRequest, imageResponse.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, audioResponse.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, videoResponse.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, mismatchResponse.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, unsupportedResponse.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, unrecognisedResponse.StatusCode);
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MEDIA_DIR", priorRoot);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [SkippableFact]
    public async Task Api_questions_attach_and_return_only_media_owned_by_the_resolved_tenant()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "media-attachment");
        var editor = await InsertAdminAsync(owner, "media-question-editor", "media-question-editor@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        var createdMedia = await InsertMediaAsync(owner, TenantA);
        var editedMedia = await InsertMediaAsync(owner, TenantA);
        var foreignMedia = await InsertMediaAsync(owner, TenantB);
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        using var createBody = JsonContent.Create(ChoiceRequest("Media create", category, mediaId: createdMedia));
        using var created = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookie, token, createBody);
        var question = await created.Content.ReadFromJsonAsync<QuestionResponse>();
        using var editBody = JsonContent.Create(ChoiceRequest("Media edit", category, mediaId: editedMedia));
        using var edited = await SendAsync(client, HttpMethod.Put, $"/api/admin/questions/{question!.Id}", cookie, token, editBody);
        var updated = await edited.Content.ReadFromJsonAsync<QuestionResponse>();
        using var fetched = await SendAsync(client, HttpMethod.Get, $"/api/admin/questions/{question.Id}", cookie);
        var reread = await fetched.Content.ReadFromJsonAsync<QuestionResponse>();
        using var listed = await SendAsync(client, HttpMethod.Get, "/api/admin/questions", cookie);
        var page = await listed.Content.ReadFromJsonAsync<QuestionListResponse>();
        using var foreignBody = JsonContent.Create(ChoiceRequest("Foreign media", category, mediaId: foreignMedia));
        using var foreign = await SendAsync(client, HttpMethod.Put, $"/api/admin/questions/{question.Id}", cookie, token, foreignBody);
        using var unknownBody = JsonContent.Create(ChoiceRequest("Unknown media", category, mediaId: Guid.NewGuid()));
        using var unknown = await SendAsync(client, HttpMethod.Put, $"/api/admin/questions/{question.Id}", cookie, token, unknownBody);
        using var unchanged = await SendAsync(client, HttpMethod.Get, $"/api/admin/questions/{question.Id}", cookie);
        var afterRejectedEdits = await unchanged.Content.ReadFromJsonAsync<QuestionResponse>();

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(createdMedia, question.MediaId);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal(editedMedia, updated!.MediaId);
        Assert.Equal(editedMedia, reread!.MediaId);
        Assert.Equal(editedMedia, Assert.Single(page!.Items).MediaId);
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(editedMedia, afterRejectedEdits!.MediaId);
    }

    [SkippableFact]
    public async Task Api_editor_can_update_list_and_delete_questions()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "geography");
        var editor = await InsertAdminAsync(owner, "question-crud", "question-crud@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        using var createBody = JsonContent.Create(ChoiceRequest("Original capital question", category));
        using var created = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookie, token, createBody);
        var id = (await created.Content.ReadFromJsonAsync<QuestionResponse>())!.Id;
        using var updateBody = JsonContent.Create(ChoiceRequest("Updated capital question", category, status: "approved"));
        using var updated = await SendAsync(client, HttpMethod.Put, $"/api/admin/questions/{id}", cookie, token, updateBody);
        using var secondBody = JsonContent.Create(ChoiceRequest("Second capital question", category, status: "approved"));
        using var second = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookie, token, secondBody);
        using var listed = await SendAsync(client, HttpMethod.Get, $"/api/admin/questions?language=en&family=trivia&kind=choice&categoryId={category}&level=1&status=approved&source=admin&text=Updated&page=1&pageSize=1", cookie);
        var page = await listed.Content.ReadFromJsonAsync<QuestionListResponse>();
        using var paged = await SendAsync(client, HttpMethod.Get, $"/api/admin/questions?language=en&family=trivia&kind=choice&categoryId={category}&level=1&status=approved&source=admin&page=2&pageSize=1", cookie);
        var secondPage = await paged.Content.ReadFromJsonAsync<QuestionListResponse>();
        using var deleted = await SendAsync(client, HttpMethod.Delete, $"/api/admin/questions/{id}", cookie, token);
        using var missing = await SendAsync(client, HttpMethod.Get, $"/api/admin/questions/{id}", cookie);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(1, page!.Total);
        Assert.Equal("Updated capital question", Assert.Single(page.Items).Prompt);
        Assert.Equal(1, Assert.Single(page.Buckets).Approved);
        Assert.Equal("Second capital question", Assert.Single(secondPage!.Items).Prompt);
        Assert.Equal(2, Assert.Single(secondPage.Buckets).Approved);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [SkippableFact]
    public async Task Api_editor_and_owner_can_set_approve_and_reject_question_status()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "status");
        var editor = await InsertAdminAsync(owner, "question-status-editor", "question-status-editor@example.test");
        var tenantOwner = await InsertAdminAsync(owner, "question-status-owner", "question-status-owner@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await MembershipAsync(owner, TenantA, tenantOwner, "owner");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var sessions = app.Services.GetRequiredService<IAdminSessionIssuer>();
        var editorCookie = await IssueCookieAsync(sessions, TenantA, editor);
        var ownerCookie = await IssueCookieAsync(sessions, TenantA, tenantOwner);
        var editorToken = await AdminAntiforgeryAsync(client, editorCookie);
        var ownerToken = await AdminAntiforgeryAsync(client, ownerCookie);
        using var createBody = JsonContent.Create(ChoiceRequest("Status question", category));
        using var created = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", editorCookie, editorToken, createBody);
        var id = (await created.Content.ReadFromJsonAsync<QuestionResponse>())!.Id;
        using var approve = await SendAsync(client, HttpMethod.Post, $"/api/admin/questions/{id}/approve", editorCookie, editorToken);
        using var reject = await SendAsync(client, HttpMethod.Post, $"/api/admin/questions/{id}/reject", ownerCookie, ownerToken);
        using var statusBody = JsonContent.Create(new { status = "draft" });
        using var setStatus = await SendAsync(client, HttpMethod.Put, $"/api/admin/questions/{id}/status", editorCookie, editorToken, statusBody);
        using var read = await SendAsync(client, HttpMethod.Get, $"/api/admin/questions/{id}", editorCookie);
        var question = await read.Content.ReadFromJsonAsync<QuestionResponse>();

        Assert.Equal(HttpStatusCode.NoContent, approve.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, reject.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, setStatus.StatusCode);
        Assert.Equal("draft", question!.Status);
    }

    [SkippableFact]
    public async Task Api_question_writes_refuse_moderators_anonymous_and_unsafe_requests()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "security");
        var editor = await InsertAdminAsync(owner, "question-security-editor", "question-security-editor@example.test");
        var moderator = await InsertAdminAsync(owner, "question-security-moderator", "question-security-moderator@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await MembershipAsync(owner, TenantA, moderator, "moderator");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var sessions = app.Services.GetRequiredService<IAdminSessionIssuer>();
        var editorCookie = await IssueCookieAsync(sessions, TenantA, editor);
        var moderatorCookie = await IssueCookieAsync(sessions, TenantA, moderator);
        var moderatorToken = await AdminAntiforgeryAsync(client, moderatorCookie);
        using var moderatorBody = JsonContent.Create(ChoiceRequest("Moderator cannot write", category));
        using var moderatorResponse = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", moderatorCookie, moderatorToken, moderatorBody);
        using var anonymousBody = JsonContent.Create(ChoiceRequest("Anonymous cannot write", category));
        using var anonymousResponse = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", content: anonymousBody);
        using var missingTokenBody = JsonContent.Create(ChoiceRequest("Missing token", category));
        using var missingToken = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", editorCookie, content: missingTokenBody);
        var editorToken = await AdminAntiforgeryAsync(client, editorCookie);
        using var wrongOriginBody = JsonContent.Create(ChoiceRequest("Wrong origin", category));
        using var wrongOrigin = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", editorCookie, editorToken, wrongOriginBody, origin: "http://foreign.example.test");

        Assert.Equal(HttpStatusCode.Forbidden, moderatorResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrongOrigin.StatusCode);
    }

    [SkippableFact]
    public async Task Api_question_validation_and_duplicate_errors_preserve_the_bank()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "validation");
        var editor = await InsertAdminAsync(owner, "question-validation", "question-validation@example.test");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, editor);
        var token = await AdminAntiforgeryAsync(client, cookie);
        using var invalidBody = JsonContent.Create(new { kind = "choice", kindVersion = 1, family = "trivia", language = "en", categoryId = category, level = 1, prompt = "Invalid", payload = new { options = new[] { "only" }, correctIndex = -1 }, status = "pending", source = "admin" });
        using var invalid = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookie, token, invalidBody);
        var invalidError = await invalid.Content.ReadFromJsonAsync<ErrorResponse>();
        var large = Enumerable.Range(0, 4).Select(index => new string((char)('a' + index), 5000)).ToArray();
        using var oversizeBody = JsonContent.Create(ChoiceRequest("Too large", category, payload: new { options = large, correctIndex = 0 }));
        using var oversize = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookie, token, oversizeBody);
        var oversizeError = await oversize.Content.ReadFromJsonAsync<ErrorResponse>();
        using var firstTopicBody = JsonContent.Create(ChoiceRequest("Topic original", category, topic: "bank-topic"));
        using var firstTopic = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookie, token, firstTopicBody);
        using var duplicateTopicBody = JsonContent.Create(ChoiceRequest("Topic duplicate", category, topic: "bank-topic"));
        using var duplicateTopic = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookie, token, duplicateTopicBody);
        var duplicateTopicError = await duplicateTopic.Content.ReadFromJsonAsync<ErrorResponse>();
        using var fingerprintBody = JsonContent.Create(ChoiceRequest("Fingerprint original", category));
        using var fingerprint = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookie, token, fingerprintBody);
        using var duplicateFingerprintBody = JsonContent.Create(ChoiceRequest("Fingerprint original", category));
        using var duplicateFingerprint = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookie, token, duplicateFingerprintBody);
        var duplicateFingerprintError = await duplicateFingerprint.Content.ReadFromJsonAsync<ErrorResponse>();
        using var listed = await SendAsync(client, HttpMethod.Get, "/api/admin/questions", cookie);
        var page = await listed.Content.ReadFromJsonAsync<QuestionListResponse>();

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains(invalidError!.Problems!, problem => problem.Code == "options.tooFew");
        Assert.Contains(invalidError.Problems!, problem => problem.Code == "correctIndex.outOfRange");
        Assert.Equal(HttpStatusCode.BadRequest, oversize.StatusCode);
        Assert.Equal("question_too_large", oversizeError!.Error);
        Assert.Equal(HttpStatusCode.Created, firstTopic.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicateTopic.StatusCode);
        Assert.Equal("duplicate_question", duplicateTopicError!.Error);
        Assert.Equal(HttpStatusCode.Created, fingerprint.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicateFingerprint.StatusCode);
        Assert.Equal("duplicate_question", duplicateFingerprintError!.Error);
        Assert.Equal(2, page!.Total);
    }

    [SkippableFact]
    public async Task Api_question_envelope_and_cross_tenant_boundaries_are_client_errors()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var categoryA = await InsertCategoryAsync(owner, TenantA, "boundary-a");
        var categoryB = await InsertCategoryAsync(owner, TenantB, "boundary-b");
        var playerB = await InsertPlayerAsync(owner, TenantB);
        var editorA = await InsertAdminAsync(owner, "question-boundary-a", "question-boundary-a@example.test");
        var editorB = await InsertAdminAsync(owner, "question-boundary-b", "question-boundary-b@example.test");
        await MembershipAsync(owner, TenantA, editorA, "editor");
        await MembershipAsync(owner, TenantB, editorB, "editor");
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var sessions = app.Services.GetRequiredService<IAdminSessionIssuer>();
        var cookieA = await IssueCookieAsync(sessions, TenantA, editorA);
        var cookieB = await IssueCookieAsync(sessions, TenantB, editorB);
        var tokenA = await AdminAntiforgeryAsync(client, cookieA);
        var tokenB = await AdminAntiforgeryAsync(client, cookieB, "questions-b.example.test");
        using var unknownKindBody = JsonContent.Create(ChoiceRequest("Unknown kind", categoryA, kind: "unknown"));
        using var unknownKind = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookieA, tokenA, unknownKindBody);
        using var invalidLanguageBody = JsonContent.Create(ChoiceRequest("Invalid language", categoryA, language: "zz"));
        using var invalidLanguage = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookieA, tokenA, invalidLanguageBody);
        using var foreignCategoryBody = JsonContent.Create(ChoiceRequest("Foreign category", categoryB));
        using var foreignCategory = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookieA, tokenA, foreignCategoryBody);
        using var foreignOwnerBody = JsonContent.Create(ChoiceRequest("Foreign owner", categoryA, source: "player", ownerId: playerB));
        using var foreignOwner = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookieA, tokenA, foreignOwnerBody);
        using var invalidStatusBody = JsonContent.Create(ChoiceRequest("Invalid status", categoryA, status: "unknown"));
        using var invalidStatus = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookieA, tokenA, invalidStatusBody);
        using var invalidLevelBody = JsonContent.Create(new { kind = "choice", kindVersion = 1, family = "trivia", language = "en", categoryId = categoryA, level = 0, prompt = "Invalid level", payload = new { options = new[] { "A", "B", "C", "D" }, correctIndex = 0 }, status = "pending", source = "admin" });
        using var invalidLevel = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookieA, tokenA, invalidLevelBody);
        using var invalidSourceBody = JsonContent.Create(new { kind = "choice", kindVersion = 1, family = "trivia", language = "en", categoryId = categoryA, level = 1, prompt = "Invalid source", payload = new { options = new[] { "A", "B", "C", "D" }, correctIndex = 0 }, status = "pending", source = "unknown" });
        using var invalidSource = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookieA, tokenA, invalidSourceBody);
        using var foreignCreateBody = JsonContent.Create(ChoiceRequest("Foreign question", categoryB));
        using var foreignCreate = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookieB, tokenB, foreignCreateBody, host: "questions-b.example.test");
        var foreignId = (await foreignCreate.Content.ReadFromJsonAsync<QuestionResponse>())!.Id;
        using var foreignRead = await SendAsync(client, HttpMethod.Get, $"/api/admin/questions/{foreignId}", cookieA);
        using var missingRead = await SendAsync(client, HttpMethod.Get, $"/api/admin/questions/{Guid.NewGuid()}", cookieA);

        Assert.Equal(HttpStatusCode.BadRequest, unknownKind.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidLanguage.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, foreignCategory.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, foreignOwner.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidStatus.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidLevel.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidSource.StatusCode);
        Assert.Equal(HttpStatusCode.Created, foreignCreate.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignRead.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingRead.StatusCode);
    }

    [SkippableFact]
    public async Task Api_category_delete_protects_referenced_questions_and_isolates_tenants()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var unused = await InsertCategoryAsync(owner, TenantA, "unused");
        var referenced = await InsertCategoryAsync(owner, TenantA, "referenced");
        var pollReferenced = await InsertCategoryAsync(owner, TenantA, "poll-referenced");
        var foreign = await InsertCategoryAsync(owner, TenantB, "foreign");
        var editorA = await InsertAdminAsync(owner, "question-category-a", "question-category-a@example.test");
        var editorB = await InsertAdminAsync(owner, "question-category-b", "question-category-b@example.test");
        await MembershipAsync(owner, TenantA, editorA, "editor");
        await MembershipAsync(owner, TenantB, editorB, "editor");
        await InsertPollQuestionAsync(owner, TenantA, pollReferenced);
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = Client();
        var sessions = app.Services.GetRequiredService<IAdminSessionIssuer>();
        var cookieA = await IssueCookieAsync(sessions, TenantA, editorA);
        var tokenA = await AdminAntiforgeryAsync(client, cookieA);
        using var createBody = JsonContent.Create(ChoiceRequest("Referenced category", referenced));
        using var create = await SendAsync(client, HttpMethod.Post, "/api/admin/questions", cookieA, tokenA, createBody);
        using var deletedUnused = await SendAsync(client, HttpMethod.Delete, $"/api/admin/categories/{unused}", cookieA, tokenA);
        using var refused = await SendAsync(client, HttpMethod.Delete, $"/api/admin/categories/{referenced}", cookieA, tokenA);
        var refusedError = await refused.Content.ReadFromJsonAsync<ErrorResponse>();
        using var pollRefused = await SendAsync(client, HttpMethod.Delete, $"/api/admin/categories/{pollReferenced}", cookieA, tokenA);
        var pollRefusedError = await pollRefused.Content.ReadFromJsonAsync<ErrorResponse>();
        using var deactivated = await SendAsync(client, HttpMethod.Post, $"/api/admin/categories/{referenced}/deactivate", cookieA, tokenA);
        using var foreignDelete = await SendAsync(client, HttpMethod.Delete, $"/api/admin/categories/{foreign}", cookieA, tokenA);

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deletedUnused.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("category_in_use", refusedError!.Error);
        Assert.Equal(HttpStatusCode.BadRequest, pollRefused.StatusCode);
        Assert.Equal("category_in_use", pollRefusedError!.Error);
        Assert.Equal(HttpStatusCode.NoContent, deactivated.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignDelete.StatusCode);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (fixture.ConnectionString is null) return;
        await using var owner = await OpenAsync("quesshi_owner");
        await ResetDatabaseAsync(owner);
    }

    private async Task<NpgsqlConnection> PrepareAsync()
    {
        await Quesshi.Server.Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var owner = await OpenAsync("quesshi_owner");
        await ResetDatabaseAsync(owner);
        await ExecuteAsync(owner, "insert into tenants (id, slug, name, default_language) values (@a, 'questions-a', 'Questions A', 'en'), (@b, 'questions-b', 'Questions B', 'en')", ("a", TenantA), ("b", TenantB));
        await ExecuteAsync(owner, "insert into tenant_languages (tenant_id, code, name, english_name) values (@a, 'en', 'English', 'English'), (@a, 'fa', 'فارسی', 'Persian'), (@b, 'en', 'English', 'English')", ("a", TenantA), ("b", TenantB));
        await ExecuteAsync(owner, "insert into tenant_hosts (tenant_id, host) values (@a, 'questions-a.example.test'), (@b, 'questions-b.example.test')", ("a", TenantA), ("b", TenantB));
        return owner;
    }

    private DatabaseSources Sources() => new(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));

    private static Task ResetDatabaseAsync(NpgsqlConnection owner) =>
        ExecuteAsync(owner, "delete from session_answers; delete from session_items; delete from session_participants; delete from sessions; delete from questions; delete from categories; delete from auth_sessions; delete from admin_memberships; delete from admins; delete from tenant_languages; delete from tenants");

    private static async Task<WebApplication> StartAsync(DatabaseSources sources)
    {
        var app = Quesshi.Server.Program.CreateApplication(sources, 11299, 30299);
        app.Urls.Add("http://127.0.0.1:50299");
        await app.StartAsync();
        await app.Services.GetRequiredService<Quesshi.Infrastructure.Clock.PostgresClock>().StartAsync();
        return app;
    }

    private static HttpClient Client() => new() { BaseAddress = new Uri("http://127.0.0.1:50299") };

    private static object ChoiceRequest(string prompt, Guid category, string status = "pending", string? topic = null, string kind = "choice", string language = "en", object? payload = null, string source = "admin", Guid? ownerId = null, Guid? mediaId = null) => new
    {
        kind, kindVersion = 1, family = "trivia", language, categoryId = category, level = 1, prompt,
        payload = payload ?? new { options = new[] { "Paris", "Lyon", "Nice", "Lille" }, correctIndex = 0 },
        topic, status, source, ownerId, mediaId,
    };

    private static MultipartFormDataContent UploadBody(byte[] bytes, string mime, string? attribution = null)
    {
        var body = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(mime);
        body.Add(file, "file", "sample");
        if (attribution is not null) body.Add(new StringContent(attribution), "attribution");
        return body;
    }

    private static MultipartFormDataContent ImportBody(Guid category, string prompt, bool? dryRun = null, string? status = null)
    {
        var body = new MultipartFormDataContent();
        body.Add(new StringContent("choice"), "kind");
        if (dryRun is not null) body.Add(new StringContent(dryRun.Value.ToString().ToLowerInvariant()), "dryRun");
        if (status is not null) body.Add(new StringContent(status), "status");
        var csv = $"language,category,level,prompt,choice1,choice2,choice3,choice4,correctIndex,status\nen,{category},2,\"{prompt}\",Paris,Lyon,Nice,Lille,0,approved\n";
        body.Add(new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(csv)), "file", "questions.csv");
        return body;
    }

    private static byte[] Png() =>
    [
        0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
        0x00, 0x00, 0x00, 0x0d, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00,
    ];

    private static byte[] Wav() =>
    [
        0x52, 0x49, 0x46, 0x46, 0x28, 0x00, 0x00, 0x00,
        0x57, 0x41, 0x56, 0x45, 0x66, 0x6d, 0x74, 0x20,
        0x10, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00,
        0x40, 0x1f, 0x00, 0x00, 0x80, 0x3e, 0x00, 0x00,
        0x02, 0x00, 0x10, 0x00, 0x64, 0x61, 0x74, 0x61,
        0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    ];

    private static async Task<Guid> InsertCategoryAsync(NpgsqlConnection owner, Guid tenant, string slug)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(owner, "insert into categories (tenant_id, id, slug, names) values (@tenant, @id, @slug, '{\"en\":\"Category\"}'::jsonb)", ("tenant", tenant), ("id", id), ("slug", slug));
        return id;
    }

    private static Task InsertPollQuestionAsync(NpgsqlConnection owner, Guid tenant, Guid category) =>
        ExecuteAsync(owner, "insert into questions (tenant_id, id, kind, kind_version, family, lang, category_id, level, prompt, payload, fingerprint, status, source) values (@tenant, @id, 'poll_choice', 1, 'poll', 'en', @category, 1, 'Poll category reference', '{\"options\":[\"yes\",\"no\"]}'::jsonb, 'poll-category-reference', 'pending', 'admin')", ("tenant", tenant), ("id", Guid.NewGuid()), ("category", category));

    private static async Task<Guid> InsertMediaAsync(NpgsqlConnection owner, Guid tenant)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(owner, "insert into media_assets (tenant_id, id, kind, storage_key, mime, bytes) values (@tenant, @id, 'image', @key, 'image/png', 1)", ("tenant", tenant), ("id", id), ("key", id.ToString("N")));
        return id;
    }

    private static async Task<Guid> InsertPlayerAsync(NpgsqlConnection owner, Guid tenant)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(owner, "insert into players (tenant_id, id, display_name, avatar_seed, lang) values (@tenant, @id, 'Question owner', 'question-owner', 'en')", ("tenant", tenant), ("id", id));
        return id;
    }

    private static async Task<Guid> InsertAdminAsync(NpgsqlConnection owner, string username, string email)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(owner, "insert into admins (id, username, email, password_hash) values (@id, @username, @email, @hash)", ("id", id), ("username", username), ("email", email), ("hash", new IdentityPasswordHasher().Hash("Strong password 2026!")));
        return id;
    }

    private static Task MembershipAsync(NpgsqlConnection owner, Guid tenant, Guid admin, string role) =>
        ExecuteAsync(owner, "insert into admin_memberships (tenant_id, admin_id, role) values (@tenant, @admin, @role)", ("tenant", tenant), ("admin", admin), ("role", role));

    private async Task<NpgsqlConnection> OpenAsync(string role)
    {
        var connection = new NpgsqlConnection(fixture.ConnectionStringForRole(role));
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> IssueCookieAsync(IAdminSessionIssuer sessions, Guid tenant, Guid admin)
    {
        var context = new DefaultHttpContext();
        Assert.True(await sessions.IssueAsync(context, tenant, admin, false));
        var header = Assert.Single(context.Response.Headers.SetCookie.ToArray())
            ?? throw new InvalidOperationException("The admin session cookie was missing.");
        var value = header["__Host-qa=".Length..] ?? throw new InvalidOperationException("The admin session cookie was malformed.");
        return Uri.UnescapeDataString(value[..value.IndexOf(';')]);
    }

    private static async Task<string> AdminAntiforgeryAsync(HttpClient client, string cookie, string host = "questions-a.example.test")
    {
        using var response = await SendAsync(client, HttpMethod.Get, "/api/admin/antiforgery", cookie, host: host);
        return (await response.Content.ReadFromJsonAsync<AntiforgeryResponse>())!.Token;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string? cookie = null, string? antiforgery = null, HttpContent? content = null, string host = "questions-a.example.test", string? origin = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Host = host;
        if (cookie is not null) request.Headers.TryAddWithoutValidation("Cookie", $"__Host-qa={cookie}");
        if (antiforgery is not null) request.Headers.TryAddWithoutValidation("X-CSRF-Admin", antiforgery);
        if (method != HttpMethod.Get) request.Headers.TryAddWithoutValidation("Origin", origin ?? $"http://{host}");
        return await client.SendAsync(request);
    }

    private sealed record AntiforgeryResponse(string Token);
    private sealed record MediaResponse(Guid Id, string Hash, string Kind, string Mime, long Bytes, JsonElement Meta, string? Attribution);
    private sealed record QuestionResponse(Guid Id, JsonElement Payload, string Status, string Prompt, Guid? MediaId);
    private sealed record QuestionListResponse(IReadOnlyList<QuestionResponse> Items, int Total, IReadOnlyList<QuestionBucketResponse> Buckets);
    private sealed record QuestionBucketResponse(int Approved, int Pending);
    private sealed record ErrorResponse(string Error, IReadOnlyList<ContentProblemResponse>? Problems);
    private sealed record ContentProblemResponse(string Code);
}
