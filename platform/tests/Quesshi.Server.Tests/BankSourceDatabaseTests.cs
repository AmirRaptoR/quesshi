using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quesshi.Application.Content;
using Quesshi.Features.Sources.Bank;
using Quesshi.Infrastructure.Database;
using Quesshi.Plugin.Api;
using Quesshi.Server;
using Quesshi.Testing;
using Xunit.Sdk;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class BankSourceDatabaseTests(PostgresFixture fixture)
{
    private static readonly Guid TenantA = Guid.Parse("baab61fc-61c2-42b5-bc27-afb47e25b9f1");
    private static readonly Guid TenantB = Guid.Parse("51c4462e-730f-4657-87e8-38a2f9981a83");

    [SkippableFact]
    public async Task Bank_draws_only_allowed_approved_tenant_language_questions_without_repeats()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var allowed = await InsertCategoryAsync(owner, TenantA, "allowed", true);
        var denied = await InsertCategoryAsync(owner, TenantA, "denied", true);
        await EnableBankAsync(owner, TenantA, new BankSettings(new Dictionary<string, IReadOnlyList<Guid>> { ["trivia"] = [allowed] }));
        var eligible = new List<Guid>();
        for (var number = 0; number < 10; number++)
        {
            eligible.Add(await InsertQuestionAsync(owner, TenantA, allowed, 1, "en", "approved", null, $"eligible-{number}"));
        }

        _ = await InsertQuestionAsync(owner, TenantA, denied, 1, "en", "approved", null, "denied");
        _ = await InsertQuestionAsync(owner, TenantA, null, 1, "en", "approved", null, "uncategorized");
        _ = await InsertQuestionAsync(owner, TenantA, allowed, 1, "en", "pending", null, "pending");
        _ = await InsertQuestionAsync(owner, TenantA, allowed, 1, "nl", "approved", null, "dutch");
        _ = await InsertQuestionAsync(owner, TenantA, allowed, 1, "en", "approved", await InsertPlayerAsync(owner, TenantA), "player");
        _ = await InsertQuestionAsync(owner, TenantB, null, 1, "en", "approved", null, "other-tenant");

        var plan = new SourceDrawPlan(10, "en", [new SourceKind("choice", "trivia")], [1], 9);
        var result = await DrawAsync(new(), plan);
        var repeated = await DrawAsync(new(), plan);

        Assert.Equal(eligible.Order(), result.Ids.Order());
        Assert.Equal(result.Ids, repeated.Ids);
    }

    [SkippableFact]
    public async Task Bank_rechecks_source_entitlement_before_the_transactional_draw()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "allowed", true);
        await EnableBankAsync(owner, TenantA, new BankSettings(new Dictionary<string, IReadOnlyList<Guid>> { ["trivia"] = [category] }));
        _ = await InsertQuestionAsync(owner, TenantA, category, 1, "en", "approved", null, "eligible");
        var bank = new BankSource();
        var plan = new SourceDrawPlan(1, "en", [new SourceKind("choice", "trivia")], [1], 9);
        await using var sources = Sources();
        var database = new TenantDb(sources);

        Assert.True(await database.InTenantAsync(TenantA, transaction => bank.AuthorizeAsync(transaction, JsonSerializer.SerializeToElement(new BankParams()), Guid.Empty)));
        await ExecuteAsync(owner, "update tenant_capabilities set enabled = false where tenant_id = @tenant and type = 'source' and key = 'bank'", ("tenant", TenantA));

        await Assert.ThrowsAsync<SourceUnavailableException>(() => database.InTenantAsync(TenantA, transaction => bank.PickAsync(transaction, JsonSerializer.SerializeToElement(new BankParams()), plan, Guid.Empty)));
    }

    [SkippableFact]
    public async Task Bank_refuses_four_distinct_requested_categories()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var categories = new List<Guid>();
        for (var number = 0; number < 4; number++) categories.Add(await InsertCategoryAsync(owner, TenantA, $"category-{number}", true));
        await EnableBankAsync(owner, TenantA, new BankSettings(new Dictionary<string, IReadOnlyList<Guid>> { ["trivia"] = categories }));

        await Assert.ThrowsAsync<SourceParameterException>(() => DrawAsync(new BankParams(categories), new SourceDrawPlan(1, "en", [new SourceKind("choice", "trivia")], [1], 9)));
    }

    [SkippableFact]
    public async Task Bank_refuses_an_active_category_outside_the_family_allow_list()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var allowed = await InsertCategoryAsync(owner, TenantA, "allowed", true);
        var denied = await InsertCategoryAsync(owner, TenantA, "denied", true);
        await EnableBankAsync(owner, TenantA, new BankSettings(new Dictionary<string, IReadOnlyList<Guid>> { ["trivia"] = [allowed] }));

        await Assert.ThrowsAsync<SourceParameterException>(() => DrawAsync(new BankParams([denied]), new SourceDrawPlan(1, "en", [new SourceKind("choice", "trivia")], [1], 9)));
    }

    [SkippableFact]
    public async Task Bank_without_requested_categories_selects_at_most_three_eligible_categories()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var categories = new List<Guid>();
        for (var number = 0; number < 4; number++)
        {
            var category = await InsertCategoryAsync(owner, TenantA, $"category-{number}", true);
            categories.Add(category);
            _ = await InsertQuestionAsync(owner, TenantA, category, 1, "en", "approved", null, $"eligible-{number}");
        }
        await EnableBankAsync(owner, TenantA, new BankSettings(new Dictionary<string, IReadOnlyList<Guid>> { ["trivia"] = categories }));

        var exception = await Assert.ThrowsAsync<NotEnoughQuestionsException>(() => DrawAsync(new(), new SourceDrawPlan(4, "en", [new SourceKind("choice", "trivia")], [1], 9)));

        Assert.Equal((3, 4), (exception.Found, exception.Needed));
    }

    [Theory]
    [InlineData(10, "1,1,2,2,3,3,4,4,5,5")]
    [InlineData(20, "1,1,1,2,2,2,2,2,3,3,3,3,4,4,4,4,4,5,5,5")]
    [InlineData(30, "1,1,1,1,2,2,2,2,2,2,2,3,3,3,3,3,3,3,3,4,4,4,4,4,4,4,5,5,5,5")]
    [InlineData(40, "1,1,1,1,1,2,2,2,2,2,2,2,2,2,2,3,3,3,3,3,3,3,3,3,3,4,4,4,4,4,4,4,4,4,4,5,5,5,5,5")]
    [InlineData(50, "1,1,1,1,1,1,1,2,2,2,2,2,2,2,2,2,2,2,2,3,3,3,3,3,3,3,3,3,3,3,3,4,4,4,4,4,4,4,4,4,4,4,4,5,5,5,5,5,5,5")]
    [InlineData(100, "1,1,1,1,1,1,1,1,1,1,1,1,1,2,2,2,2,2,2,2,2,2,2,2,2,2,2,2,2,2,2,2,2,2,2,2,2,2,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,4,4,4,4,4,4,4,4,4,4,4,4,4,4,4,4,4,4,4,4,4,4,4,4,4,5,5,5,5,5,5,5,5,5,5,5,5,5")]
    public async Task Bank_matches_the_legacy_level_ramp_for_each_supported_plan_length(int count, string expectedLevels)
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "allowed", true);
        await EnableBankAsync(owner, TenantA, new BankSettings(new Dictionary<string, IReadOnlyList<Guid>> { ["trivia"] = [category] }));
        var expected = expectedLevels.Split(',').Select(int.Parse).ToArray();
        for (var index = 0; index < expected.Length; index++)
            _ = await InsertQuestionAsync(owner, TenantA, category, expected[index], "en", "approved", null, $"level-{index}");

        var result = await DrawAsync(new BankParams([category]), new SourceDrawPlan(count, "en", [new SourceKind("choice", "trivia")], [1, 2, 3, 4, 5], 9));

        Assert.Equal(expected, await ReadLevelsAsync(owner, result.Ids));
    }

    [SkippableFact]
    public async Task Bank_rotates_requested_categories_for_each_slot()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var categories = new[]
        {
            await InsertCategoryAsync(owner, TenantA, "a", true),
            await InsertCategoryAsync(owner, TenantA, "b", true),
            await InsertCategoryAsync(owner, TenantA, "c", true),
        };
        await EnableBankAsync(owner, TenantA, new BankSettings(new Dictionary<string, IReadOnlyList<Guid>> { ["trivia"] = categories }));
        foreach (var category in categories)
            for (var level = 1; level <= 5; level++)
                _ = await InsertQuestionAsync(owner, TenantA, category, level, "en", "approved", null, $"{category}-{level}");

        var result = await DrawAsync(new BankParams(categories), new SourceDrawPlan(6, "en", [new SourceKind("choice", "trivia")], [1, 2, 3, 4, 5], 9));

        var cycle = categories.Order().ToArray();
        Assert.Equal([cycle[0], cycle[1], cycle[2], cycle[0], cycle[1], cycle[2]], await ReadCategoriesAsync(owner, result.Ids));
    }

    [SkippableFact]
    public async Task Bank_sorts_and_deduplicates_requested_levels_before_the_restricted_ramp()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "allowed", true);
        await EnableBankAsync(owner, TenantA, new BankSettings(new Dictionary<string, IReadOnlyList<Guid>> { ["trivia"] = [category] }));
        for (var number = 0; number < 5; number++)
        {
            _ = await InsertQuestionAsync(owner, TenantA, category, 1, "en", "approved", null, $"easy-{number}");
            _ = await InsertQuestionAsync(owner, TenantA, category, 5, "en", "approved", null, $"hard-{number}");
        }

        var result = await DrawAsync(new BankParams([category], [5, 1, 5]), new SourceDrawPlan(10, "en", [new SourceKind("choice", "trivia")], [1, 5], 9));

        Assert.Equal([1, 1, 1, 1, 1, 5, 5, 5, 5, 5], await ReadLevelsAsync(owner, result.Ids));
    }

    [SkippableFact]
    public async Task Bank_reports_found_and_needed_when_selected_buckets_are_unfillable()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "allowed", true);
        await EnableBankAsync(owner, TenantA, new BankSettings(new Dictionary<string, IReadOnlyList<Guid>> { ["trivia"] = [category] }));
        _ = await InsertQuestionAsync(owner, TenantA, category, 1, "en", "approved", null, "eligible");

        var exception = await Assert.ThrowsAsync<NotEnoughQuestionsException>(() => DrawAsync(new(), new SourceDrawPlan(2, "en", [new SourceKind("choice", "trivia")], [1], 9)));

        Assert.Equal((1, 2), (exception.Found, exception.Needed));
    }

    [SkippableFact]
    public async Task Bank_falls_back_within_the_selected_categories_and_allowed_levels()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var first = await InsertCategoryAsync(owner, TenantA, "first", true);
        var second = await InsertCategoryAsync(owner, TenantA, "second", true);
        await EnableBankAsync(owner, TenantA, new BankSettings(new Dictionary<string, IReadOnlyList<Guid>> { ["trivia"] = [first, second] }));
        _ = await InsertQuestionAsync(owner, TenantA, second, 2, "en", "approved", null, "fallback-one");
        _ = await InsertQuestionAsync(owner, TenantA, second, 2, "en", "approved", null, "fallback-two");

        var result = await DrawAsync(new BankParams([first, second]), new SourceDrawPlan(2, "en", [new SourceKind("choice", "trivia")], [1, 2], 9));

        Assert.Equal([2, 2], await ReadLevelsAsync(owner, result.Ids));
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("pending")]
    [InlineData("rejected")]
    public async Task Bank_never_draws_a_non_approved_question(string status)
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "allowed", true);
        await EnableBankAsync(owner, TenantA, new BankSettings(new Dictionary<string, IReadOnlyList<Guid>> { ["trivia"] = [category] }));
        var approved = await InsertQuestionAsync(owner, TenantA, category, 1, "en", "approved", null, "approved");
        _ = await InsertQuestionAsync(owner, TenantA, category, 1, "en", status, null, status);

        var result = await DrawAsync(new(), new SourceDrawPlan(1, "en", [new SourceKind("choice", "trivia")], [1], 9));

        Assert.Equal([approved], result.Ids);
    }

    [SkippableFact]
    public async Task Bank_never_draws_a_kind_excluded_by_the_plan()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "allowed", true);
        await EnableBankAsync(owner, TenantA, new BankSettings(new Dictionary<string, IReadOnlyList<Guid>> { ["trivia"] = [category] }));
        var included = await InsertQuestionAsync(owner, TenantA, category, 1, "en", "approved", null, "order", "order", "trivia");
        _ = await InsertQuestionAsync(owner, TenantA, category, 1, "en", "approved", null, "choice");

        var result = await DrawAsync(new(), new SourceDrawPlan(1, "en", [new SourceKind("order", "trivia")], [1], 9));

        Assert.Equal([included], result.Ids);
    }

    [SkippableFact]
    public async Task Bank_seeded_sampling_never_duplicates_a_question_across_varied_draws()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var category = await InsertCategoryAsync(owner, TenantA, "allowed", true);
        await EnableBankAsync(owner, TenantA, new BankSettings(new Dictionary<string, IReadOnlyList<Guid>> { ["trivia"] = [category] }));
        for (var number = 0; number < 10; number++)
            _ = await InsertQuestionAsync(owner, TenantA, category, 1, "en", "approved", null, $"eligible-{number}");

        for (var seed = 0; seed < 200; seed++)
        {
            var result = await DrawAsync(new(), new SourceDrawPlan(10, "en", [new SourceKind("choice", "trivia")], [1], seed));
            Assert.Equal(10, result.Ids.Distinct().Count());
        }
    }

    [SkippableFact]
    public async Task Source_result_validator_rejects_a_cross_tenant_question_id()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await EnableKindAsync(owner, TenantA);
        var foreign = await InsertQuestionAsync(owner, TenantB, null, 1, "en", "approved", null, "foreign");
        await using var sources = Sources();
        await using var app = Program.CreateApplication(sources, 19180, 39180);
        var database = app.Services.GetRequiredService<ITenantDb>();
        var validator = app.Services.GetRequiredService<SourceResultValidator>();

        await Assert.ThrowsAsync<InvalidSourceResultException>(() => database.InTenantAsync(TenantA, transaction => validator.ValidateAsync(transaction, new PickedQuestionIds([foreign]), new SourceDrawPlan(1, "en", [new SourceKind("choice", "trivia")], [1], 9))));
    }

    [SkippableFact]
    public async Task Source_result_validator_rejects_question_ids_with_the_wrong_count()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await EnableKindAsync(owner, TenantA);

        await AssertInvalidSourceResultAsync(new PickedQuestionIds([]), new SourceDrawPlan(1, "en", [new SourceKind("choice", "trivia")], [1], 9));
    }

    [SkippableFact]
    public async Task Source_result_validator_rejects_an_unavailable_question_id()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await EnableKindAsync(owner, TenantA);

        await AssertInvalidSourceResultAsync(new PickedQuestionIds([Guid.NewGuid()]), new SourceDrawPlan(1, "en", [new SourceKind("choice", "trivia")], [1], 9));
    }

    [SkippableFact]
    public async Task Source_result_validator_rejects_a_question_id_with_a_kind_not_accepted_by_the_plan()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await EnableKindAsync(owner, TenantA);
        var question = await InsertQuestionAsync(owner, TenantA, null, 1, "en", "approved", null, "choice");

        await AssertInvalidSourceResultAsync(new PickedQuestionIds([question]), new SourceDrawPlan(1, "en", [new SourceKind("order", "trivia")], [1], 9));
    }

    [SkippableFact]
    public async Task Source_result_validator_rejects_a_question_id_with_a_disabled_kind()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var question = await InsertQuestionAsync(owner, TenantA, null, 1, "en", "approved", null, "choice");

        await AssertInvalidSourceResultAsync(new PickedQuestionIds([question]), new SourceDrawPlan(1, "en", [new SourceKind("choice", "trivia")], [1], 9));
    }

    [SkippableFact]
    public async Task Source_result_validator_rejects_a_question_id_with_an_unregistered_kind_version()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await EnableKindAsync(owner, TenantA);
        var question = await InsertQuestionAsync(owner, TenantA, null, 1, "en", "approved", null, "choice", version: 2);

        await AssertInvalidSourceResultAsync(new PickedQuestionIds([question]), new SourceDrawPlan(1, "en", [new SourceKind("choice", "trivia")], [1], 9));
    }

    [SkippableFact]
    public async Task Source_result_validator_rejects_a_malformed_ready_snapshot()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await EnableKindAsync(owner, TenantA);
        await using var sources = Sources();
        await using var app = Program.CreateApplication(sources, 19181, 39181);
        var database = app.Services.GetRequiredService<ITenantDb>();
        var validator = app.Services.GetRequiredService<SourceResultValidator>();
        var snapshot = new ReadySnapshot("choice", 1, "trivia", 1, null, null, "Prompt", JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { }));

        await Assert.ThrowsAsync<InvalidSourceResultException>(() => database.InTenantAsync(TenantA, transaction => validator.ValidateAsync(transaction, new PickedSnapshots([snapshot]), new SourceDrawPlan(1, "en", [new SourceKind("choice", "trivia")], [1], 9))));
    }

    [SkippableFact]
    public async Task Source_result_validator_rejects_ready_snapshots_with_the_wrong_count()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await EnableKindAsync(owner, TenantA);

        await AssertInvalidSourceResultAsync(new PickedSnapshots([]), new SourceDrawPlan(1, "en", [new SourceKind("choice", "trivia")], [1], 9));
    }

    [SkippableFact]
    public async Task Source_result_validator_rejects_a_ready_snapshot_with_a_kind_not_accepted_by_the_plan()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await EnableKindAsync(owner, TenantA);

        await AssertInvalidSourceResultAsync(new PickedSnapshots([ValidSnapshot()]), new SourceDrawPlan(1, "en", [new SourceKind("order", "trivia")], [1], 9));
    }

    [SkippableFact]
    public async Task Source_result_validator_rejects_a_ready_snapshot_with_a_disabled_kind()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();

        await AssertInvalidSourceResultAsync(new PickedSnapshots([ValidSnapshot()]), new SourceDrawPlan(1, "en", [new SourceKind("choice", "trivia")], [1], 9));
    }

    [SkippableFact]
    public async Task Source_result_validator_rejects_a_ready_snapshot_with_an_unregistered_kind_version()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await EnableKindAsync(owner, TenantA);

        await AssertInvalidSourceResultAsync(new PickedSnapshots([ValidSnapshot(version: 2)]), new SourceDrawPlan(1, "en", [new SourceKind("choice", "trivia")], [1], 9));
    }

    [SkippableFact]
    public async Task Source_result_validator_rejects_a_ready_snapshot_referencing_foreign_tenant_media()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await EnableKindAsync(owner, TenantA);
        var media = await InsertMediaAsync(owner, TenantB);

        await AssertInvalidSourceResultAsync(new PickedSnapshots([ValidSnapshot(mediaId: media)]), new SourceDrawPlan(1, "en", [new SourceKind("choice", "trivia")], [1], 9));
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("resolved")]
    [InlineData("card")]
    [InlineData("reveal")]
    public async Task Source_result_validator_rejects_a_ready_snapshot_with_malformed_kind_data(string part)
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await EnableKindAsync(owner, TenantA);
        var malformed = Json("\"malformed\"");
        var snapshot = part switch
        {
            "payload" => ValidSnapshot(payload: malformed),
            "resolved" => ValidSnapshot(resolved: malformed),
            "card" => ValidSnapshot(card: malformed),
            _ => ValidSnapshot(reveal: malformed),
        };

        await AssertInvalidSourceResultAsync(new PickedSnapshots([snapshot]), new SourceDrawPlan(1, "en", [new SourceKind("choice", "trivia")], [1], 9));
    }

    private async Task<PickedQuestionIds> DrawAsync(BankParams parameters, SourceDrawPlan plan)
    {
        await using var sources = Sources();
        var database = new TenantDb(sources);
        var result = await database.InTenantAsync(TenantA, transaction => new BankSource().PickAsync(transaction, JsonSerializer.SerializeToElement(parameters), plan, Guid.Empty));
        return Assert.IsType<PickedQuestionIds>(result);
    }

    private async Task AssertInvalidSourceResultAsync(Picked picked, SourceDrawPlan plan)
    {
        await using var sources = Sources();
        await using var app = Program.CreateApplication(sources, 19182, 39182);
        var database = app.Services.GetRequiredService<ITenantDb>();
        var validator = app.Services.GetRequiredService<SourceResultValidator>();

        await Assert.ThrowsAsync<InvalidSourceResultException>(() => database.InTenantAsync(TenantA, transaction => validator.ValidateAsync(transaction, picked, plan)));
    }

    private async Task<NpgsqlConnection> PrepareAsync()
    {
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var owner = await OpenAsync("quesshi_owner");
        await ExecuteAsync(owner, "delete from session_answers; delete from session_items; delete from session_participants; delete from sessions; delete from questions; delete from categories; delete from players; delete from tenant_capabilities; delete from tenant_languages; delete from tenants");
        await ExecuteAsync(owner, "insert into tenants (id, slug, name, default_language) values (@a, 'bank-a', 'Bank A', 'en'), (@b, 'bank-b', 'Bank B', 'en')", ("a", TenantA), ("b", TenantB));
        await ExecuteAsync(owner, "insert into tenant_languages (tenant_id, code, name, english_name) values (@a, 'en', 'English', 'English'), (@a, 'nl', 'Nederlands', 'Dutch'), (@b, 'en', 'English', 'English')", ("a", TenantA), ("b", TenantB));
        return owner;
    }

    private async Task EnableBankAsync(NpgsqlConnection owner, Guid tenant, BankSettings settings) =>
        await ExecuteAsync(owner, "insert into tenant_capabilities (tenant_id, type, key, entitled, enabled, settings) values (@tenant, 'source', 'bank', true, true, @settings::jsonb)", ("tenant", tenant), ("settings", JsonSerializer.Serialize(settings)));

    private static Task EnableKindAsync(NpgsqlConnection owner, Guid tenant) =>
        ExecuteAsync(owner, "insert into tenant_capabilities (tenant_id, type, key, entitled, enabled) values (@tenant, 'kind', 'choice', true, true)", ("tenant", tenant));

    private static async Task<Guid> InsertCategoryAsync(NpgsqlConnection owner, Guid tenant, string slug, bool active)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(owner, "insert into categories (tenant_id, id, slug, names, is_active) values (@tenant, @id, @slug, '{\"en\":\"Category\"}'::jsonb, @active)", ("tenant", tenant), ("id", id), ("slug", slug), ("active", active));
        return id;
    }

    private static async Task<Guid> InsertQuestionAsync(NpgsqlConnection owner, Guid tenant, Guid? category, int level, string language, string status, Guid? ownerId, string prompt, string kind = "choice", string family = "trivia", int version = 1)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(owner, "insert into questions (tenant_id, id, kind, kind_version, family, lang, category_id, level, prompt, payload, fingerprint, status, source, owner_id) values (@tenant, @id, @kind, @version, @family, @language, @category, @level, @prompt, '{\"options\":[\"a\",\"b\",\"c\",\"d\"],\"correctIndex\":0}'::jsonb, @prompt, @status, @source, @owner)", ("tenant", tenant), ("id", id), ("kind", kind), ("version", version), ("family", family), ("language", language), ("category", (object?)category ?? DBNull.Value), ("level", level), ("prompt", prompt), ("status", status), ("source", ownerId is null ? "admin" : "player"), ("owner", (object?)ownerId ?? DBNull.Value));
        return id;
    }

    private static async Task<Guid> InsertMediaAsync(NpgsqlConnection owner, Guid tenant)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(owner, "insert into media_assets (tenant_id, id, kind, storage_key, mime, bytes) values (@tenant, @id, 'image', @storageKey, 'image/png', 1)", ("tenant", tenant), ("id", id), ("storageKey", id.ToString("N")));
        return id;
    }

    private static ReadySnapshot ValidSnapshot(int version = 1, Guid? mediaId = null, JsonElement? payload = null, JsonElement? resolved = null, JsonElement? card = null, JsonElement? reveal = null) => new(
        "choice", version, "trivia", 1, null, mediaId, "Prompt",
        payload ?? Json("{\"options\":[\"a\",\"b\",\"c\",\"d\"],\"correctIndex\":0}"),
        resolved ?? Json("{}"),
        card ?? Json("{\"options\":[\"a\",\"b\",\"c\",\"d\"]}"),
        reveal ?? Json("{\"correctIndex\":0,\"correctOption\":\"a\"}"));

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static async Task<IReadOnlyList<int>> ReadLevelsAsync(NpgsqlConnection connection, IReadOnlyList<Guid> ids)
    {
        await using var command = new NpgsqlCommand("select id, level from questions where id = any(@ids)", connection);
        command.Parameters.AddWithValue("ids", ids.ToArray());
        await using var reader = await command.ExecuteReaderAsync();
        var levels = new Dictionary<Guid, int>();
        while (await reader.ReadAsync()) levels[reader.GetGuid(0)] = reader.GetInt32(1);
        return ids.Select(id => levels[id]).ToArray();
    }

    private static async Task<IReadOnlyList<Guid>> ReadCategoriesAsync(NpgsqlConnection connection, IReadOnlyList<Guid> ids)
    {
        await using var command = new NpgsqlCommand("select id, category_id from questions where id = any(@ids)", connection);
        command.Parameters.AddWithValue("ids", ids.ToArray());
        await using var reader = await command.ExecuteReaderAsync();
        var categories = new Dictionary<Guid, Guid>();
        while (await reader.ReadAsync()) categories[reader.GetGuid(0)] = reader.GetGuid(1);
        return ids.Select(id => categories[id]).ToArray();
    }

    private static async Task<Guid> InsertPlayerAsync(NpgsqlConnection owner, Guid tenant)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(owner, "insert into players (tenant_id, id, display_name, avatar_seed, lang) values (@tenant, @id, 'Player', 'seed', 'en')", ("tenant", tenant), ("id", id));
        return id;
    }

    private DatabaseSources Sources() => new(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));

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
}
