using System.Text.Json;
using Dapper;
using Npgsql;
using Quesshi.Application;
using Quesshi.Application.Jobs;
using Quesshi.Features.Features.Reports;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Jobs;
using Quesshi.Plugin.Api;
using Quesshi.Server;
using Quesshi.Server.Accounts;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

public sealed class ReportsSubmissionMaintenanceDatabaseTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly IReadOnlySet<string> GlobalTables = new HashSet<string>(StringComparer.Ordinal)
    {
        "tenants", "tenant_hosts", "tenant_languages", "tenant_capabilities", "accounts", "auth_sessions", "jobs"
    };

    [SkippableFact]
    public async Task Question_reports_is_registered_as_a_tenant_isolated_table()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        await using var owner = await OpenAsync("quesshi_owner");

        await TenantIsolationTestHelper.AssertTenantCatalogueAsync(owner, GlobalTables);
    }

    [SkippableFact]
    public async Task Player_cleanup_removes_question_reports_through_the_registered_feature_handler()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var sources = new DatabaseSources(
            fixture.ConnectionStringForRole("quesshi_app"),
            fixture.ConnectionStringForRole("quesshi_system"));
        var registry = FeatureRegistry.Create([new ReportsFeature()]);
        var effects = new DurableEvents(registry, new TenantJobs(), new TenantLocks(), new NullSessionEffectsNotifier());
        var cleanup = new CorePlayerCleanup(new TenantLocks(), effects);
        registry.Jobs.Register("core", "cleanup-player", effects.HandleCleanupJobAsync);

        await cleanup.HandleAsync(new JobContext(0, seeded.TenantId, "core", "player-cleanup",
            JsonSerializer.SerializeToElement(new PlayerCleanupPayload(seeded.PlayerId)), new TenantDb(sources)), CancellationToken.None);
        var worker = new JobWorker(new PostgresJobStore(sources), registry.Jobs, new TenantDb(sources), new OffCapabilities());
        await worker.RunPassAsync(1, TimeSpan.FromMinutes(1));

        Assert.Equal(0L, await TenantScalarAsync<long>(seeded.TenantId,
            "select count(*) from question_reports where player_id = @playerId", new { seeded.PlayerId }));
    }

    private async Task<(Guid TenantId, Guid PlayerId)> SeedAsync()
    {
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var tenantId = Guid.NewGuid();
        var playerId = Guid.NewGuid();
        var questionId = Guid.NewGuid();
        await using var owner = await OpenAsync("quesshi_owner");
        await ExecuteAsync(owner, """
            insert into tenants (id, slug, name, default_language)
            values (@tenantId, @slug, 'Reports', 'en');
            insert into tenant_languages (tenant_id, code, name, english_name)
            values (@tenantId, 'en', 'English', 'English');
            insert into players (tenant_id, id, display_name, avatar_seed, lang)
            values (@tenantId, @playerId, 'Player', 'player', 'en');
            insert into questions (tenant_id, id, kind, kind_version, family, lang, level, prompt, payload, fingerprint, status, source)
            values (@tenantId, @questionId, 'choice', 1, 'trivia', 'en', 1, 'Question',
                    '{"options":["a","b","c","d"],"correctIndex":0}'::jsonb, 'reports-cleanup', 'approved', 'admin');
            insert into question_reports (tenant_id, question_id, player_id, reason)
            values (@tenantId, @questionId, @playerId, 'Other');
            """, new { tenantId, playerId, questionId, slug = $"reports-{tenantId:N}" });
        return (tenantId, playerId);
    }

    private async Task<T> TenantScalarAsync<T>(Guid tenantId, string sql, object parameters)
    {
        await using var sources = new DatabaseSources(
            fixture.ConnectionStringForRole("quesshi_app"),
            fixture.ConnectionStringForRole("quesshi_system"));
        return (await new TenantDb(sources).InTenantAsync(tenantId,
            transaction => transaction.QuerySingleOrDefaultAsync<T>(sql, parameters, CancellationToken.None), readOnly: true))!;
    }

    private async Task<NpgsqlConnection> OpenAsync(string role)
    {
        var connection = new NpgsqlConnection(fixture.ConnectionStringForRole(role));
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, object parameters)
    {
        await connection.ExecuteAsync(new CommandDefinition(sql, parameters));
    }

    private sealed class OffCapabilities : ICapabilities
    {
        public Task<bool> IsOnAsync(
            Guid tenantId,
            CapabilityType type,
            string key,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }

        public Task<TSettings> GetSettingsAsync<TSettings>(
            Guid tenantId,
            CapabilityType type,
            string key,
            CancellationToken cancellationToken = default)
            where TSettings : notnull
        {
            throw new NotSupportedException();
        }

        public Task SetAsync<TSettings>(
            Guid tenantId,
            CapabilityType type,
            string key,
            bool enabled,
            TSettings settings,
            CancellationToken cancellationToken = default)
            where TSettings : notnull
        {
            throw new NotSupportedException();
        }
    }
}
