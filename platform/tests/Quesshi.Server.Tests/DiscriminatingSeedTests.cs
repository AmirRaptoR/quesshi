using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quesshi.Application;
using Quesshi.Application.Content;
using Quesshi.Application.Jobs;
using Quesshi.Engine.Contracts;
using Quesshi.Features.Sources.Bank;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Jobs;
using Quesshi.Infrastructure.Media;
using Quesshi.Plugin.Api;
using Quesshi.Server;
using Quesshi.Server.Accounts;
using Quesshi.Server.Tests.TestSeeds;
using Quesshi.Testing;
using Xunit.Sdk;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class DiscriminatingSeedTests(PostgresFixture fixture)
{
    private const long BankDrawSeed = 336;

    [SkippableFact]
    public async Task Tenant_scoped_content_query_excludes_content_seeded_for_another_tenant()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var primary = await TenantSeed.CreateAsync(owner, "seed-content-primary");
        var foreign = await DiscriminatingSeeds.AddForeignTenantContentAsync(owner, "seed-content-foreign");
        await using var sources = Sources();
        await using var app = CreateApplication(sources);
        var content = app.Services.GetRequiredService<IContent>();
        var database = app.Services.GetRequiredService<ITenantDb>();

        var question = await database.InTenantAsync(primary.Id, transaction => content.GetAsync(transaction, foreign.Question.Id));

        Assert.Null(question);
    }

    [SkippableFact]
    public async Task Bank_selection_never_returns_an_eligible_question_in_an_unselected_category()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var tenant = await TenantSeed.CreateAsync(owner, "seed-bank-category");
        var selectedCategory = await CategorySeed.CreateAsync(owner, tenant, "selected");
        _ = await QuestionSeed.CreateAsync(owner, tenant, selectedCategory, "approved");
        _ = await QuestionSeed.CreateAsync(owner, tenant, selectedCategory, "approved");
        var unselected = await DiscriminatingSeeds.AddUnselectedCategoryWithEligibleQuestionAsync(owner, tenant);
        await EnableBankAsync(owner, tenant, [selectedCategory.Id, unselected.Category.Id]);

        var result = await DrawAsync(tenant.Id, new BankParams([selectedCategory.Id]), count: 2);

        Assert.DoesNotContain(unselected.Question.Id, result.Ids);
    }

    [SkippableFact]
    public async Task Bank_selection_excludes_an_ineligible_question_that_sorts_first_in_the_draw()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var tenant = await TenantSeed.CreateAsync(owner, "seed-bank-status");
        var category = await CategorySeed.CreateAsync(owner, tenant, "selected");
        var approved = await QuestionSeed.CreateAsync(owner, tenant, category, "approved");
        _ = await DiscriminatingSeeds.AddIneligibleQuestionBeforeAsync(owner, tenant, category, approved.Id, BankDrawSeed);
        await EnableBankAsync(owner, tenant, [category.Id]);

        var result = await DrawAsync(tenant.Id, new BankParams([category.Id]));

        Assert.Equal(new[] { approved.Id }, result.Ids);
    }

    [SkippableFact]
    public async Task Active_session_query_excludes_a_finished_session_seed()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var tenant = await TenantSeed.CreateAsync(owner, "seed-finished-session");
        var now = new DateTimeOffset(2040, 1, 31, 12, 0, 0, TimeSpan.Zero);
        var finished = await DiscriminatingSeeds.AddFinishedSessionAsync(owner, tenant, now - TimeSpan.FromDays(31), now);
        await using var sources = Sources();
        var database = new TenantDb(sources);
        var purge = CreateGuestPurge(sources, now);

        await purge.HandleAsync(new JobContext(0, tenant.Id, "core", "guest-purge", JsonSerializer.SerializeToElement(new { }), database), CancellationToken.None);
        var deleted = await database.InTenantAsync(tenant.Id, transaction => transaction.QuerySingleOrDefaultAsync<bool>(
            "select deleted_at is not null from players where id = @guest", new { guest = finished.GuestId }, CancellationToken.None), readOnly: true);

        Assert.True(deleted);
    }

    [SkippableFact]
    public async Task Tenant_scoped_media_lookup_excludes_media_seeded_for_another_tenant()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var primary = await TenantSeed.CreateAsync(owner, "seed-media-primary");
        var foreignTenant = await TenantSeed.CreateAsync(owner, "seed-media-foreign");
        var foreignMedia = await DiscriminatingSeeds.AddForeignMediaAsync(owner, foreignTenant, new byte[] { 137, 80, 78, 71 });
        await using var sources = Sources();
        var store = new LocalMediaStore(new TenantDb(sources), new ConfigurationBuilder().Build());

        var media = await store.FindByIdAsync(primary.Id, foreignMedia.Id);

        Assert.Null(media);
    }

    private async Task<NpgsqlConnection> PrepareAsync()
    {
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var owner = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_owner"));
        await owner.OpenAsync();
        return owner;
    }

    private async Task<PickedQuestionIds> DrawAsync(Guid tenantId, BankParams parameters, int count = 1)
    {
        await using var sources = Sources();
        var database = new TenantDb(sources);
        var plan = new SourceDrawPlan(count, "en", [new SourceKind("choice", "trivia")], [1], BankDrawSeed);
        var result = await database.InTenantAsync(tenantId, transaction => new BankSource().PickAsync(transaction, JsonSerializer.SerializeToElement(parameters), plan, Guid.Empty));
        return Assert.IsType<PickedQuestionIds>(result);
    }

    private static Task EnableBankAsync(NpgsqlConnection owner, SeedTenant tenant, IReadOnlyList<Guid> categories) =>
        SeedSql.ExecuteAsync(owner, """
            insert into tenant_capabilities (tenant_id, type, key, entitled, enabled, settings)
            values (@tenant, 'source', 'bank', true, true, @settings::jsonb)
            """, CancellationToken.None,
            ("tenant", tenant.Id), ("settings", JsonSerializer.Serialize(new BankSettings(new Dictionary<string, IReadOnlyList<Guid>> { ["trivia"] = categories }))));

    private static GuestPurge CreateGuestPurge(DatabaseSources sources, DateTimeOffset now)
    {
        var locks = new TenantLocks();
        var registry = FeatureRegistry.Create([]);
        var effects = new DurableEvents(registry, new TenantJobs(), locks, new NullSessionEffectsNotifier());
        return new GuestPurge(locks, new CorePlayerCleanup(locks, effects), new TenantJobs(), new SystemDb(sources), new TestClock(now));
    }

    private DatabaseSources Sources() => new(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));

    private static WebApplication CreateApplication(DatabaseSources sources) => Program.CreateApplication(sources, 11336, 30336);
}
