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

        var result = await DrawAsync(new(), new SourceDrawPlan(10, "en", [new SourceKind("choice", "trivia")], [1], 9));

        Assert.Equal(eligible.Order(), result.Ids.Order());
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

    private async Task<PickedQuestionIds> DrawAsync(BankParams parameters, SourceDrawPlan plan)
    {
        await using var sources = Sources();
        var database = new TenantDb(sources);
        var result = await database.InTenantAsync(TenantA, transaction => new BankSource().PickAsync(transaction, JsonSerializer.SerializeToElement(parameters), plan, Guid.Empty));
        return Assert.IsType<PickedQuestionIds>(result);
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

    private static async Task<Guid> InsertQuestionAsync(NpgsqlConnection owner, Guid tenant, Guid? category, int level, string language, string status, Guid? ownerId, string prompt)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(owner, "insert into questions (tenant_id, id, kind, kind_version, family, lang, category_id, level, prompt, payload, fingerprint, status, source, owner_id) values (@tenant, @id, 'choice', 1, 'trivia', @language, @category, @level, @prompt, '{\"options\":[\"a\",\"b\",\"c\",\"d\"],\"correctIndex\":0}'::jsonb, @prompt, @status, @source, @owner)", ("tenant", tenant), ("id", id), ("language", language), ("category", (object?)category ?? DBNull.Value), ("level", level), ("prompt", prompt), ("status", status), ("source", ownerId is null ? "admin" : "player"), ("owner", (object?)ownerId ?? DBNull.Value));
        return id;
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
