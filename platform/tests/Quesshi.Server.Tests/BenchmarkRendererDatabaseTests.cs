using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Application.Sessions;
using Quesshi.Bench.Live;
using Quesshi.Infrastructure.Database;
using Quesshi.Testing;
using Xunit.Sdk;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class BenchmarkRendererDatabaseTests(PostgresFixture fixture)
{
    [SkippableFact]
    public async Task Server_renderer_renders_the_persisted_benchmark_choice_card()
    {
        Skip.If(fixture.ConnectionString is null, $"Set {PostgresFixture.AdminConnectionStringEnvironmentVariable} to run PostgreSQL tests locally.");
        var ownerConnection = fixture.ConnectionStringForRole("quesshi_owner");
        await Program.MigrateAsync(ownerConnection);
        await using var owner = new Npgsql.NpgsqlConnection(ownerConnection);
        await owner.OpenAsync();

        var tenant = await BenchmarkFixture.SeedTenantAsync(ownerConnection, "bench-renderer.test");
        Exception? testFailure = null;
        try
        {
            var playerId = Guid.NewGuid();
            await owner.ExecuteAsync("insert into players (tenant_id, id, display_name, avatar_seed, lang) values (@tenant, @player, 'Benchmark player', 'bench', 'en')", new { tenant = tenant.Id, player = playerId });
            var session = await BenchmarkFixture.SeedSessionAsync(ownerConnection, tenant, new BenchmarkOptions(20, LiveScenario.Cold, null), [playerId]);
            var benchmarkItem = BenchmarkItem.Create(0);
            var persistedPrompt = await owner.QuerySingleAsync<string>("select prompt from session_items where tenant_id = @tenant and session_id = @session and slot = 0", new { tenant = tenant.Id, session = session.Id });

            await using var sources = new DatabaseSources(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));
            var state = Assert.IsType<SessionState>(await new SessionSnapshotLoader(new TenantDb(sources)).LoadAsync(tenant.Id, session.Id));
            await using var app = Program.CreateApplication(sources, TestPorts.Allocate(), TestPorts.Allocate());
            var renderer = app.Services.GetRequiredService<IViewRenderer>();

            Assert.Equal(benchmarkItem.Prompt, persistedPrompt);
            Assert.Equal(200, persistedPrompt.Length);
            using var rendered = JsonDocument.Parse(renderer.Render(state, new BenchLiveFormat(), playerId.ToString(), DateTimeOffset.UtcNow).Json);
            var renderedOptions = rendered.RootElement.GetProperty("Body").GetProperty("card").GetProperty("options")
                .EnumerateArray().Select(option => option.GetString()!).ToArray();
            Assert.Equal(benchmarkItem.Options, renderedOptions);
            Assert.All(renderedOptions, option => Assert.Equal(80, option.Length));
        }
        catch (Exception exception)
        {
            testFailure = exception;
            throw;
        }
        finally
        {
            try
            {
                await owner.ExecuteAsync("delete from sessions where tenant_id = @tenant; delete from players where tenant_id = @tenant; delete from tenants where id = @tenant", new { tenant = tenant.Id });
            }
            catch when (testFailure is not null)
            {
                // Preserve the assertion or setup failure that caused cleanup to run.
            }
        }
    }
}
