using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Security;
using Quesshi.Server.Security;
using Quesshi.Testing;
using Xunit.Sdk;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class CategoryDatabaseTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly Guid TenantA = Guid.Parse("928182ee-562b-4d9e-b0eb-1c3234648b25");
    private static readonly Guid TenantB = Guid.Parse("0a2ff03e-9ed8-4ce9-a274-4415a2139764");

    [SkippableFact]
    public async Task Categories_migration_creates_the_documented_tenant_table_without_seed_data()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();

        var definition = await ScalarAsync<string>(owner, """
            select string_agg(column_name || ':' || udt_name, ',' order by ordinal_position)
            from information_schema.columns
            where table_schema = 'public' and table_name = 'categories'
            """);

        Assert.Equal("tenant_id:uuid,id:uuid,slug:text,names:jsonb,icon:text,color:text,is_active:bool,sort_order:int4", definition);
        Assert.True(await ScalarAsync<bool>(owner, "select exists (select 1 from pg_indexes where tablename = 'categories' and indexdef like '%UNIQUE%tenant_id, slug%')"));
        Assert.Equal(0L, await ScalarAsync<long>(owner, "select count(*) from categories"));
    }

    [SkippableFact]
    public async Task Categories_migration_grants_the_app_role_and_isolates_tenants()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var app = await OpenAsync("quesshi_app");
        await ExecuteAsync(app, "select set_config('app.tenant_id', @tenant::text, false)", ("tenant", TenantA));
        await ExecuteAsync(app, "insert into categories (tenant_id, id, slug, names) values (@tenant, @id, 'tenant-a', '{\"en\": \"Tenant A\"}'::jsonb)", ("tenant", TenantA), ("id", Guid.NewGuid()));

        Assert.True(await ScalarAsync<bool>(owner, """
            select has_table_privilege('quesshi_app', 'categories', 'select')
                and has_table_privilege('quesshi_app', 'categories', 'insert')
                and has_table_privilege('quesshi_app', 'categories', 'update')
                and has_table_privilege('quesshi_app', 'categories', 'delete')
            """));
        Assert.False(await ScalarAsync<bool>(owner, "select has_table_privilege('quesshi_system', 'categories', 'select')"));
        Assert.True(await ScalarAsync<bool>(owner, "select relrowsecurity from pg_class where oid = 'categories'::regclass"));
        Assert.Equal("owner:core", await ScalarAsync<string>(owner, "select obj_description('categories'::regclass)"));
        await ExecuteAsync(app, "select set_config('app.tenant_id', @tenant::text, false)", ("tenant", TenantB));
        Assert.Equal(0L, await ScalarAsync<long>(app, "select count(*) from categories"));
    }

    [SkippableFact]
    public async Task Api_editor_can_manage_categories_and_list_inactive_entries()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        var tenantOwner = await InsertAdminAsync(owner, "category-owner", "category-owner@example.test");
        var editor = await InsertAdminAsync(owner, "category-editor", "category-editor@example.test");
        await MembershipAsync(owner, TenantA, tenantOwner, "owner");
        await MembershipAsync(owner, TenantA, editor, "editor");
        await using var app = await StartAsync(sources);
        using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50297") };
        var sessions = app.Services.GetRequiredService<IAdminSessionIssuer>();
        var ownerCookie = await IssueCookieAsync(sessions, TenantA, tenantOwner);
        var editorCookie = await IssueCookieAsync(sessions, TenantA, editor);
        var ownerToken = await AdminAntiforgeryAsync(client, ownerCookie);
        var editorToken = await AdminAntiforgeryAsync(client, editorCookie);
        using var firstBody = JsonContent.Create(new { slug = "history", names = new Dictionary<string, string> { ["en"] = "History", ["fa"] = "تاریخ" }, icon = "book", color = "#336699", sortOrder = 5 });
        using var first = await SendAsync(client, HttpMethod.Post, "/api/admin/categories", ownerCookie, ownerToken, firstBody);
        var firstId = (await first.Content.ReadFromJsonAsync<CategoryResponse>())!.Id;
        using var secondBody = JsonContent.Create(new { slug = "science", names = new Dictionary<string, string> { ["en"] = "Science", ["fa"] = "علم" }, icon = "flask", color = "#00aa00", sortOrder = 6 });
        using var second = await SendAsync(client, HttpMethod.Post, "/api/admin/categories", editorCookie, editorToken, secondBody);
        var secondId = (await second.Content.ReadFromJsonAsync<CategoryResponse>())!.Id;
        using var updateBody = JsonContent.Create(new { slug = "world-history", names = new Dictionary<string, string> { ["en"] = "World history", ["fa"] = "تاریخ جهان" }, icon = "globe", color = "#123456", sortOrder = 9 });
        using var update = await SendAsync(client, HttpMethod.Put, $"/api/admin/categories/{firstId}", editorCookie, editorToken, updateBody);
        using var reorderBody = JsonContent.Create(new { ids = new[] { secondId, firstId } });
        using var reorder = await SendAsync(client, HttpMethod.Put, "/api/admin/categories/reorder", editorCookie, editorToken, reorderBody);
        using var deactivate = await SendAsync(client, HttpMethod.Post, $"/api/admin/categories/{firstId}/deactivate", editorCookie, editorToken);
        using var listed = await SendAsync(client, HttpMethod.Get, "/api/admin/categories", editorCookie);
        var listedCategories = await listed.Content.ReadFromJsonAsync<CategoryAdminResponse[]>()
            ?? throw new InvalidOperationException("The category administration response was empty.");
        var history = listedCategories.Single(category => category.Slug == "world-history");
        using var reactivateBody = JsonContent.Create(new { slug = "world-history", names = new Dictionary<string, string> { ["en"] = "World history", ["fa"] = "تاریخ جهان" }, icon = "globe", color = "#123456", sortOrder = 1, isActive = true });
        using var reactivate = await SendAsync(client, HttpMethod.Put, $"/api/admin/categories/{firstId}", editorCookie, editorToken, reactivateBody);
        var reactivated = await reactivate.Content.ReadFromJsonAsync<CategoryAdminResponse>();

        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.OK, HttpStatusCode.NoContent, HttpStatusCode.NoContent, HttpStatusCode.OK, HttpStatusCode.OK),
            (first.StatusCode, second.StatusCode, update.StatusCode, reorder.StatusCode, deactivate.StatusCode, listed.StatusCode, reactivate.StatusCode));
        Assert.Equal(("تاریخ جهان", "globe", "#123456", false, 1),
            (history.Names["fa"], history.Icon, history.Color, history.IsActive, history.SortOrder));
        Assert.True(reactivated!.IsActive);
        Assert.Equal(["science", "world-history"], listedCategories.Select(category => category.Slug));
    }

    [SkippableFact]
    public async Task Api_category_mutations_refuse_a_moderator_and_an_unauthenticated_caller()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        var moderator = await InsertAdminAsync(owner, "category-moderator", "category-moderator@example.test");
        await MembershipAsync(owner, TenantA, moderator, "moderator");
        await using var app = await StartAsync(sources);
        using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50297") };
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, moderator);
        var token = await AdminAntiforgeryAsync(client, cookie);
        using var moderatorBody = JsonContent.Create(new { slug = "forbidden", names = new Dictionary<string, string> { ["en"] = "Forbidden" }, icon = "x", color = "#000000", sortOrder = 0 });
        using var moderatorResponse = await SendAsync(client, HttpMethod.Post, "/api/admin/categories", cookie, token, moderatorBody);
        using var anonymousBody = JsonContent.Create(new { slug = "anonymous", names = new Dictionary<string, string> { ["en"] = "Anonymous" }, icon = "x", color = "#000000", sortOrder = 0 });
        using var anonymousResponse = await SendAsync(client, HttpMethod.Post, "/api/admin/categories", null, null, anonymousBody);

        Assert.Equal(HttpStatusCode.Forbidden, moderatorResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
    }

    [SkippableFact]
    public async Task Api_bank_category_settings_refuse_moderator_and_unauthenticated_callers_without_changes()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var triviaCategory = await AddCategoryAsync(owner, TenantA, "history", "{\"en\":\"History\"}", true, 0);
        var pollCategory = await AddCategoryAsync(owner, TenantA, "opinion", "{\"en\":\"Opinion\"}", true, 1);
        await AddBankCapabilityAsync(owner, TenantA, $"{{\"categoriesByFamily\":{{\"trivia\":[\"{triviaCategory}\"],\"poll\":[\"{pollCategory}\"]}}}}");
        await using var sources = Sources();
        var moderator = await InsertAdminAsync(owner, "category-settings-moderator", "category-settings-moderator@example.test");
        await MembershipAsync(owner, TenantA, moderator, "moderator");
        await using var app = await StartAsync(sources);
        using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50297") };
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, moderator);
        var token = await AdminAntiforgeryAsync(client, cookie);
        using var moderatorGet = await SendAsync(client, HttpMethod.Get, "/api/admin/categories/bank-settings", cookie);
        using var moderatorBody = JsonContent.Create(new { categoriesByFamily = new Dictionary<string, Guid[]> { ["trivia"] = [pollCategory], ["poll"] = [triviaCategory] } });
        using var moderatorPut = await SendAsync(client, HttpMethod.Put, "/api/admin/categories/bank-settings", cookie, token, moderatorBody);
        using var anonymousBody = JsonContent.Create(new { categoriesByFamily = new Dictionary<string, Guid[]> { ["trivia"] = [pollCategory], ["poll"] = [triviaCategory] } });
        using var anonymousPut = await SendAsync(client, HttpMethod.Put, "/api/admin/categories/bank-settings", null, null, anonymousBody);

        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized, triviaCategory, pollCategory),
            (moderatorGet.StatusCode, moderatorPut.StatusCode, anonymousPut.StatusCode, await BankCategoryAsync(owner, TenantA, "trivia"), await BankCategoryAsync(owner, TenantA, "poll")));
    }

    [SkippableFact]
    public async Task Api_player_categories_filter_sort_fall_back_and_isolate_the_tenant()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await InsertCategoryAsync(owner, TenantA, "later", "{\"en\":\"Later\",\"fa\":\"دیرتر\"}", true, 2);
        await InsertCategoryAsync(owner, TenantA, "fallback", "{\"en\":\"Fallback\",\"fa\":\"جایگزین\"}", true, 1);
        await InsertCategoryAsync(owner, TenantA, "inactive", "{\"en\":\"Inactive\"}", false, 0);
        await InsertCategoryAsync(owner, TenantB, "foreign", "{\"en\":\"Foreign\"}", true, 0);
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50297") };
        using var response = await SendAsync(client, HttpMethod.Get, "/api/categories?language=nl", host: "categories-a.example.test");
        var categories = await response.Content.ReadFromJsonAsync<CategoryDisplayResponse[]>()
            ?? throw new InvalidOperationException("The category response was empty.");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["fallback", "later"], categories.Select(category => category.Slug));
        Assert.Equal("Fallback", categories[0].Name);
        Assert.DoesNotContain(categories, category => category.Slug is "inactive" or "foreign");
    }

    [SkippableFact]
    public async Task Api_player_categories_use_the_requested_tenant_language_when_it_is_present()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await InsertCategoryAsync(owner, TenantA, "history", "{\"en\":\"History\",\"fa\":\"تاریخ\"}", true, 0);
        await using var sources = Sources();
        await using var app = await StartAsync(sources);
        using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50297") };
        using var response = await SendAsync(client, HttpMethod.Get, "/api/categories?language=fa", host: "categories-a.example.test");
        var categories = await response.Content.ReadFromJsonAsync<CategoryDisplayResponse[]>()
            ?? throw new InvalidOperationException("The category response was empty.");

        Assert.Equal("تاریخ", Assert.Single(categories).Name);
    }

    [SkippableFact]
    public async Task Api_bank_category_settings_persist_for_the_current_tenant()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var trivia = await AddCategoryAsync(owner, TenantA, "history", "{\"en\":\"History\"}", true, 0);
        var poll = await AddCategoryAsync(owner, TenantA, "opinion", "{\"en\":\"Opinion\"}", true, 1);
        await AddBankCapabilityAsync(owner, TenantA);
        var admin = await InsertAdminAsync(owner, "category-settings-owner", "category-settings-owner@example.test");
        await MembershipAsync(owner, TenantA, admin, "owner");
        await using var app = await StartAsync(Sources());
        using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50297") };
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, admin);
        var token = await AdminAntiforgeryAsync(client, cookie);
        using var body = JsonContent.Create(new { categoriesByFamily = new Dictionary<string, Guid[]> { ["trivia"] = [trivia], ["poll"] = [poll] } });
        using var saved = await SendAsync(client, HttpMethod.Put, "/api/admin/categories/bank-settings", cookie, token, body);
        using var loaded = await SendAsync(client, HttpMethod.Get, "/api/admin/categories/bank-settings", cookie);
        var settings = await loaded.Content.ReadFromJsonAsync<BankSettingsResponse>();

        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.OK, trivia, poll),
            (saved.StatusCode, loaded.StatusCode, settings!.CategoriesByFamily["trivia"].Single(), settings.CategoriesByFamily["poll"].Single()));
    }

    [SkippableFact]
    public async Task Api_bank_category_settings_reject_unknown_categories_without_changing_the_saved_list()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var trivia = await AddCategoryAsync(owner, TenantA, "history", "{\"en\":\"History\"}", true, 0);
        await AddBankCapabilityAsync(owner, TenantA, $"{{\"categoriesByFamily\":{{\"trivia\":[\"{trivia}\"],\"poll\":[]}}}}");
        var admin = await InsertAdminAsync(owner, "category-invalid-owner", "category-invalid-owner@example.test");
        await MembershipAsync(owner, TenantA, admin, "owner");
        await using var app = await StartAsync(Sources());
        using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50297") };
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, admin);
        var token = await AdminAntiforgeryAsync(client, cookie);
        using var body = JsonContent.Create(new { categoriesByFamily = new Dictionary<string, Guid[]> { ["trivia"] = [Guid.NewGuid()], ["poll"] = [] } });
        using var rejected = await SendAsync(client, HttpMethod.Put, "/api/admin/categories/bank-settings", cookie, token, body);
        using var loaded = await SendAsync(client, HttpMethod.Get, "/api/admin/categories/bank-settings", cookie);
        var settings = await loaded.Content.ReadFromJsonAsync<BankSettingsResponse>();

        Assert.Equal((HttpStatusCode.BadRequest, trivia), (rejected.StatusCode, settings!.CategoriesByFamily["trivia"].Single()));
    }

    [SkippableFact]
    public async Task Api_bank_category_settings_reject_a_foreign_category_without_partially_changing_either_tenant()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var tenantATrivia = await AddCategoryAsync(owner, TenantA, "history", "{\"en\":\"History\"}", true, 0);
        var tenantAPoll = await AddCategoryAsync(owner, TenantA, "opinion", "{\"en\":\"Opinion\"}", true, 1);
        var tenantAChangedPoll = await AddCategoryAsync(owner, TenantA, "survey", "{\"en\":\"Survey\"}", true, 2);
        var tenantBTrivia = await AddCategoryAsync(owner, TenantB, "foreign-history", "{\"en\":\"Foreign history\"}", true, 0);
        var tenantBPoll = await AddCategoryAsync(owner, TenantB, "foreign-opinion", "{\"en\":\"Foreign opinion\"}", true, 1);
        await AddBankCapabilityAsync(owner, TenantA, $"{{\"categoriesByFamily\":{{\"trivia\":[\"{tenantATrivia}\"],\"poll\":[\"{tenantAPoll}\"]}}}}");
        await AddBankCapabilityAsync(owner, TenantB, $"{{\"categoriesByFamily\":{{\"trivia\":[\"{tenantBTrivia}\"],\"poll\":[\"{tenantBPoll}\"]}}}}");
        var admin = await InsertAdminAsync(owner, "category-foreign-owner", "category-foreign-owner@example.test");
        await MembershipAsync(owner, TenantA, admin, "owner");
        await using var app = await StartAsync(Sources());
        using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50297") };
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, admin);
        var token = await AdminAntiforgeryAsync(client, cookie);
        using var body = JsonContent.Create(new { categoriesByFamily = new Dictionary<string, Guid[]> { ["trivia"] = [tenantBTrivia], ["poll"] = [tenantAChangedPoll] } });
        using var rejected = await SendAsync(client, HttpMethod.Put, "/api/admin/categories/bank-settings", cookie, token, body);
        var error = await rejected.Content.ReadFromJsonAsync<ErrorResponse>();

        Assert.Equal((HttpStatusCode.BadRequest, "invalid_bank_category_settings", tenantATrivia, tenantAPoll, tenantBTrivia, tenantBPoll),
            (rejected.StatusCode, error!.Error, await BankCategoryAsync(owner, TenantA, "trivia"), await BankCategoryAsync(owner, TenantA, "poll"), await BankCategoryAsync(owner, TenantB, "trivia"), await BankCategoryAsync(owner, TenantB, "poll")));
    }

    [SkippableFact]
    public async Task Api_bank_category_settings_do_not_cross_tenant_boundaries()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var tenantACategory = await AddCategoryAsync(owner, TenantA, "history", "{\"en\":\"History\"}", true, 0);
        var tenantBCategory = await AddCategoryAsync(owner, TenantB, "foreign", "{\"en\":\"Foreign\"}", true, 0);
        await AddBankCapabilityAsync(owner, TenantA);
        await AddBankCapabilityAsync(owner, TenantB, $"{{\"categoriesByFamily\":{{\"trivia\":[\"{tenantBCategory}\"],\"poll\":[]}}}}");
        var admin = await InsertAdminAsync(owner, "category-isolation-owner", "category-isolation-owner@example.test");
        await MembershipAsync(owner, TenantA, admin, "owner");
        await using var app = await StartAsync(Sources());
        using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50297") };
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, admin);
        var token = await AdminAntiforgeryAsync(client, cookie);
        using var body = JsonContent.Create(new { categoriesByFamily = new Dictionary<string, Guid[]> { ["trivia"] = [tenantACategory], ["poll"] = [] } });
        using var changed = await SendAsync(client, HttpMethod.Put, "/api/admin/categories/bank-settings", cookie, token, body);

        Assert.Equal((HttpStatusCode.NoContent, tenantBCategory), (changed.StatusCode, await BankCategoryAsync(owner, TenantB, "trivia")));
    }

    [SkippableFact]
    public async Task Api_player_categories_filter_the_saved_family_list_and_exclude_inactive_categories()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var visible = await AddCategoryAsync(owner, TenantA, "history", "{\"en\":\"History\",\"fa\":\"تاریخ\"}", true, 2);
        var inactive = await AddCategoryAsync(owner, TenantA, "retired", "{\"en\":\"Retired\",\"fa\":\"قدیمی\"}", false, 0);
        await AddCategoryAsync(owner, TenantA, "science", "{\"en\":\"Science\",\"fa\":\"علم\"}", true, 1);
        await AddBankCapabilityAsync(owner, TenantA, $"{{\"categoriesByFamily\":{{\"trivia\":[\"{visible}\",\"{inactive}\"],\"poll\":[]}}}}");
        await using var app = await StartAsync(Sources());
        using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50297") };
        using var response = await SendAsync(client, HttpMethod.Get, "/api/categories?family=trivia&language=fa", host: "categories-a.example.test");
        var categories = await response.Content.ReadFromJsonAsync<CategoryDisplayResponse[]>();

        var category = Assert.Single(categories!);

        Assert.Equal((HttpStatusCode.OK, "history", "تاریخ"), (response.StatusCode, category.Slug, category.Name));
    }

    [SkippableFact]
    public async Task Api_player_categories_returns_poll_categories_after_the_editor_changes_the_allow_list()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var trivia = await AddCategoryAsync(owner, TenantA, "history", "{\"en\":\"History\"}", true, 0);
        var poll = await AddCategoryAsync(owner, TenantA, "opinion", "{\"en\":\"Opinion\"}", true, 1);
        await AddBankCapabilityAsync(owner, TenantA);
        var admin = await InsertAdminAsync(owner, "category-refresh-owner", "category-refresh-owner@example.test");
        await MembershipAsync(owner, TenantA, admin, "owner");
        await using var app = await StartAsync(Sources());
        using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50297") };
        var cookie = await IssueCookieAsync(app.Services.GetRequiredService<IAdminSessionIssuer>(), TenantA, admin);
        var token = await AdminAntiforgeryAsync(client, cookie);
        using var body = JsonContent.Create(new { categoriesByFamily = new Dictionary<string, Guid[]> { ["trivia"] = [trivia], ["poll"] = [poll] } });
        using var changed = await SendAsync(client, HttpMethod.Put, "/api/admin/categories/bank-settings", cookie, token, body);
        using var response = await SendAsync(client, HttpMethod.Get, "/api/categories?family=poll", host: "categories-a.example.test");
        var categories = await response.Content.ReadFromJsonAsync<CategoryDisplayResponse[]>();

        Assert.Equal((HttpStatusCode.NoContent, "opinion"), (changed.StatusCode, Assert.Single(categories!).Slug));
    }

    [SkippableFact]
    public async Task Api_player_categories_reject_an_unknown_family_and_isolate_family_lists_by_tenant()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        var tenantA = await AddCategoryAsync(owner, TenantA, "history", "{\"en\":\"History\"}", true, 0);
        var tenantB = await AddCategoryAsync(owner, TenantB, "foreign", "{\"en\":\"Foreign\"}", true, 0);
        await AddBankCapabilityAsync(owner, TenantA, $"{{\"categoriesByFamily\":{{\"trivia\":[\"{tenantA}\"],\"poll\":[]}}}}");
        await AddBankCapabilityAsync(owner, TenantB, $"{{\"categoriesByFamily\":{{\"trivia\":[\"{tenantB}\"],\"poll\":[]}}}}");
        await using var app = await StartAsync(Sources());
        using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50297") };
        using var unknown = await SendAsync(client, HttpMethod.Get, "/api/categories?family=unknown", host: "categories-a.example.test");
        using var tenantBResponse = await SendAsync(client, HttpMethod.Get, "/api/categories?family=trivia", host: "categories-b.example.test");
        var categories = await tenantBResponse.Content.ReadFromJsonAsync<CategoryDisplayResponse[]>();

        Assert.Equal((HttpStatusCode.BadRequest, "foreign"), (unknown.StatusCode, Assert.Single(categories!).Slug));
    }

    [SkippableFact]
    public async Task Api_invalid_and_duplicate_category_writes_preserve_data_and_allow_a_foreign_tenant_slug()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await using var owner = await PrepareAsync();
        await using var sources = Sources();
        var ownerA = await InsertAdminAsync(owner, "category-owner-a", "category-owner-a@example.test");
        var ownerB = await InsertAdminAsync(owner, "category-owner-b", "category-owner-b@example.test");
        await MembershipAsync(owner, TenantA, ownerA, "owner");
        await MembershipAsync(owner, TenantB, ownerB, "owner");
        await using var app = await StartAsync(sources);
        using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:50297") };
        var sessions = app.Services.GetRequiredService<IAdminSessionIssuer>();
        var cookieA = await IssueCookieAsync(sessions, TenantA, ownerA);
        var cookieB = await IssueCookieAsync(sessions, TenantB, ownerB);
        var tokenA = await AdminAntiforgeryAsync(client, cookieA);
        var tokenB = await AdminAntiforgeryAsync(client, cookieB, "categories-b.example.test");
        using var originalBody = JsonContent.Create(new { slug = "history", names = new Dictionary<string, string> { ["en"] = "History", ["fa"] = "تاریخ" }, icon = "book", color = "#111111", sortOrder = 3 });
        using var original = await SendAsync(client, HttpMethod.Post, "/api/admin/categories", cookieA, tokenA, originalBody);
        var originalId = (await original.Content.ReadFromJsonAsync<CategoryResponse>())!.Id;
        using var canonicalBody = JsonContent.Create(new { slug = "News / 2026", names = new Dictionary<string, string> { ["en"] = "News" }, icon = "newspaper", color = "#444444", sortOrder = 4 });
        using var canonical = await SendAsync(client, HttpMethod.Post, "/api/admin/categories", cookieA, tokenA, canonicalBody);
        var canonicalCategory = await canonical.Content.ReadFromJsonAsync<CategoryAdminResponse>();
        using var emptyCanonicalBody = JsonContent.Create(new { slug = "///", names = new Dictionary<string, string> { ["en"] = "Invalid" }, icon = "x", color = "#000000", sortOrder = 0 });
        using var emptyCanonical = await SendAsync(client, HttpMethod.Post, "/api/admin/categories", cookieA, tokenA, emptyCanonicalBody);
        using var conflictingBody = JsonContent.Create(new { slug = "geography", names = new Dictionary<string, string> { ["en"] = "Geography" }, icon = "map", color = "#222222", sortOrder = 4 });
        using var conflicting = await SendAsync(client, HttpMethod.Post, "/api/admin/categories", cookieA, tokenA, conflictingBody);
        using var duplicateUpdateBody = JsonContent.Create(new { slug = "geography", names = new Dictionary<string, string> { ["en"] = "Changed" }, icon = "changed", color = "#333333", sortOrder = 5 });
        using var duplicateUpdate = await SendAsync(client, HttpMethod.Put, $"/api/admin/categories/{originalId}", cookieA, tokenA, duplicateUpdateBody);
        using var invalidUpdateBody = JsonContent.Create(new { slug = "history", names = new Dictionary<string, string> { ["nl"] = "Ongeldig" }, icon = "changed", color = "#333333", sortOrder = 5 });
        using var invalidUpdate = await SendAsync(client, HttpMethod.Put, $"/api/admin/categories/{originalId}", cookieA, tokenA, invalidUpdateBody);
        using var foreignBody = JsonContent.Create(new { slug = "history", names = new Dictionary<string, string> { ["en"] = "History B" }, icon = "book", color = "#111111", sortOrder = 0 });
        using var foreign = await SendAsync(client, HttpMethod.Post, "/api/admin/categories", cookieB, tokenB, foreignBody, host: "categories-b.example.test");

        Assert.Equal(HttpStatusCode.Created, canonical.StatusCode);
        Assert.Equal("news--2026", canonicalCategory!.Slug);
        Assert.Equal(HttpStatusCode.BadRequest, emptyCanonical.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicateUpdate.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidUpdate.StatusCode);
        Assert.Equal(HttpStatusCode.Created, foreign.StatusCode);
        Assert.Equal(("history", "{\"en\": \"History\", \"fa\": \"تاریخ\"}", "book", "#111111", 3), await CategoryStateAsync(owner, TenantA, originalId));
        Assert.Equal(1L, await ScalarAsync<long>(owner, "select count(*) from categories where tenant_id = @tenant and slug = 'history'", ("tenant", TenantB)));
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
        await ExecuteAsync(owner, "insert into tenants (id, slug, name, default_language) values (@a, 'categories-a', 'Categories A', 'en'), (@b, 'categories-b', 'Categories B', 'en')", ("a", TenantA), ("b", TenantB));
        await ExecuteAsync(owner, "insert into tenant_languages (tenant_id, code, name, english_name) values (@a, 'en', 'English', 'English'), (@a, 'fa', 'فارسی', 'Persian'), (@a, 'nl', 'Nederlands', 'Dutch'), (@b, 'en', 'English', 'English'), (@b, 'fa', 'فارسی', 'Persian')", ("a", TenantA), ("b", TenantB));
        await ExecuteAsync(owner, "insert into tenant_hosts (tenant_id, host) values (@a, 'categories-a.example.test'), (@b, 'categories-b.example.test')", ("a", TenantA), ("b", TenantB));
        return owner;
    }

    private DatabaseSources Sources() => new(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));

    private static Task ResetDatabaseAsync(NpgsqlConnection owner) =>
        ExecuteAsync(owner, "delete from session_participants; delete from sessions; delete from auth_sessions; delete from admin_memberships; delete from admins; delete from categories; delete from tenant_languages; delete from tenants");

    private static async Task<WebApplication> StartAsync(DatabaseSources sources)
    {
        var app = Quesshi.Server.Program.CreateApplication(sources, 11297, 30297);
        app.Urls.Add("http://127.0.0.1:50297");
        await app.StartAsync();
        await app.Services.GetRequiredService<Quesshi.Infrastructure.Clock.PostgresClock>().StartAsync();
        return app;
    }

    private static async Task<Guid> InsertAdminAsync(NpgsqlConnection owner, string username, string email)
    {
        var id = Guid.NewGuid();
        var hasher = new IdentityPasswordHasher();
        await ExecuteAsync(owner, "insert into admins (id, username, email, password_hash) values (@id, @username, @email, @hash)", ("id", id), ("username", username), ("email", email), ("hash", hasher.Hash("Strong password 2026!")));
        return id;
    }

    private static Task MembershipAsync(NpgsqlConnection owner, Guid tenant, Guid admin, string role) =>
        ExecuteAsync(owner, "insert into admin_memberships (tenant_id, admin_id, role) values (@tenant, @admin, @role)", ("tenant", tenant), ("admin", admin), ("role", role));

    private static Task InsertCategoryAsync(NpgsqlConnection owner, Guid tenant, string slug, string names, bool active, int sortOrder) =>
        ExecuteAsync(owner, "insert into categories (tenant_id, id, slug, names, is_active, sort_order) values (@tenant, @id, @slug, @names::jsonb, @active, @sortOrder)", ("tenant", tenant), ("id", Guid.NewGuid()), ("slug", slug), ("names", names), ("active", active), ("sortOrder", sortOrder));

    private static async Task<Guid> AddCategoryAsync(NpgsqlConnection owner, Guid tenant, string slug, string names, bool active, int sortOrder)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(owner, "insert into categories (tenant_id, id, slug, names, is_active, sort_order) values (@tenant, @id, @slug, @names::jsonb, @active, @sortOrder)",
            ("tenant", tenant), ("id", id), ("slug", slug), ("names", names), ("active", active), ("sortOrder", sortOrder));
        return id;
    }

    private static Task AddBankCapabilityAsync(NpgsqlConnection owner, Guid tenant, string settings = "{}") =>
        ExecuteAsync(owner, """
            insert into tenant_capabilities (tenant_id, type, key, entitled, enabled, settings)
            values (@tenant, 'source', 'bank', true, true, @settings::jsonb)
            """, ("tenant", tenant), ("settings", settings));

    private static async Task<Guid> BankCategoryAsync(NpgsqlConnection owner, Guid tenant, string family)
    {
        await using var command = new NpgsqlCommand("""
            select value::uuid
            from tenant_capabilities, jsonb_array_elements_text(settings -> 'categoriesByFamily' -> @family) as value
            where tenant_id = @tenant and type = 'source' and key = 'bank'
            """, owner);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("family", family);
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<(string Slug, string Names, string Icon, string Color, int SortOrder)> CategoryStateAsync(NpgsqlConnection owner, Guid tenant, Guid id)
    {
        await using var command = new NpgsqlCommand("select slug, names::text, icon, color, sort_order from categories where tenant_id = @tenant and id = @id", owner);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4));
    }

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

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        var result = (await command.ExecuteScalarAsync())!;
        return result is T typed ? typed : (T)Convert.ChangeType(result, typeof(T), CultureInfo.InvariantCulture);
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

    private static async Task<string> AdminAntiforgeryAsync(HttpClient client, string cookie, string host = "categories-a.example.test")
    {
        using var response = await SendAsync(client, HttpMethod.Get, "/api/admin/antiforgery", cookie, host: host);
        return (await response.Content.ReadFromJsonAsync<AntiforgeryResponse>())!.Token;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string? cookie = null, string? antiforgery = null, HttpContent? content = null, string host = "categories-a.example.test")
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Host = host;
        if (cookie is not null) request.Headers.TryAddWithoutValidation("Cookie", $"__Host-qa={cookie}");
        if (antiforgery is not null) request.Headers.TryAddWithoutValidation("X-CSRF-Admin", antiforgery);
        if (method != HttpMethod.Get) request.Headers.TryAddWithoutValidation("Origin", $"http://{host}");
        return await client.SendAsync(request);
    }

    private sealed record AntiforgeryResponse(string Token);
    private sealed record CategoryResponse(Guid Id);
    private sealed record CategoryAdminResponse(string Slug, Dictionary<string, string> Names, string Icon, string Color, bool IsActive, int SortOrder);
    private sealed record CategoryDisplayResponse(string Slug, string Name);
    private sealed record BankSettingsResponse(Dictionary<string, Guid[]> CategoriesByFamily);
    private sealed record ErrorResponse(string Error);
}
