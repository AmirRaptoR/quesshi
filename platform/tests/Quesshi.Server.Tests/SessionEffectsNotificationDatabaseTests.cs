using Npgsql;
using Quesshi.Application;
using Quesshi.Application.Jobs;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Jobs;
using Quesshi.Infrastructure.Streaming;
using Quesshi.Plugin.Api;
using Quesshi.Server;
using Quesshi.Testing;
using Xunit.Sdk;

namespace Quesshi.Server.Tests;

public sealed class SessionEffectsNotificationDatabaseTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task System_job_transition_notifies_only_its_tenant_when_session_ids_collide()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var session = Guid.NewGuid();
        var feature = new CollisionFeature();
        var events = new DurableEvents(FeatureRegistry.Create([feature]), new TenantJobs(), new TenantLocks(), new NullSessionEffectsNotifier());
        await CreateSessionAsync(tenant, Guid.NewGuid(), session);
        await CreateSessionAsync(otherTenant, Guid.NewGuid(), session);
        await SetCapabilityAsync(tenant, feature.Id, enabled: true);
        await ExecuteSystemAsync("""
            insert into session_effects(tenant_id, session_id, revision)
            values (@tenant, @session, 7)
            """, ("tenant", otherTenant), ("session", session));
        await using var sources = Sources();
        await new TenantDb(sources).InTenantAsync(tenant, async transaction =>
        {
            await events.EmitAsync(transaction, new SessionEnded(session));
            return 0;
        });
        var jobId = await JobIdForFeatureAsync(tenant, feature.Id);
        await ExecuteSystemAsync("update jobs set max_attempts = 1 where id = @jobId", ("jobId", jobId));
        await using var listener = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_system"));
        var notifications = new List<string>();
        listener.Notification += (_, args) => notifications.Add(args.Payload);
        await listener.OpenAsync();
        await using (var listen = new NpgsqlCommand("listen quesshi_effects", listener))
        {
            await listen.ExecuteNonQueryAsync();
        }

        var store = new PostgresJobStore(sources, new PostgresSessionEffectsNotifier());
        var lease = Assert.Single(await store.ClaimAsync(1, TimeSpan.FromMinutes(1)));
        Assert.True(await store.FailAsync(lease.Id, lease.LockedBy, lease.Attempts, lease.MaxAttempts, "dead effect"));

        using (var first = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
        {
            await listener.WaitAsync(first.Token);
        }
        using (var remaining = new CancellationTokenSource(TimeSpan.FromMilliseconds(250)))
        {
            try { await listener.WaitAsync(remaining.Token); }
            catch (OperationCanceledException) when (remaining.IsCancellationRequested) { }
        }

        Assert.Equal([$"effects:{tenant}:{session}:1"], notifications);
    }

    private DatabaseSources Sources() => new(
        fixture.ConnectionStringForRole("quesshi_app"),
        fixture.ConnectionStringForRole("quesshi_system"));

    private async Task EnsureTenantAsync(Guid tenant) =>
        await ExecuteSystemAsync("""
            insert into tenants (id, slug, name, default_language)
            values (@tenant, @slug, @slug, 'en')
            on conflict (id) do nothing
            """, ("tenant", tenant), ("slug", $"effects-{tenant:N}"));

    private async Task CreateSessionAsync(Guid tenant, Guid player, Guid session)
    {
        await EnsureTenantAsync(tenant);
        await using var sources = Sources();
        var database = new TenantDb(sources);
        await database.InTenantAsync(tenant, async transaction =>
        {
            await transaction.ExecuteAsync("""
                insert into players (tenant_id, id, display_name, avatar_seed, lang)
                values (@tenant, @player, 'Effects', 'effects', 'en');
                insert into sessions (tenant_id, id, code, format, owner_id, lang, status, capacity, content, phase, started_at, ended_at)
                values (@tenant, @session, @code, 'test', @player, 'en', 'finished', 1, '{}'::jsonb, 'finished', now(), now());
                insert into session_participants (tenant_id, session_id, player_id, seat)
                values (@tenant, @session, @player, 0);
                """, new { tenant, player, session, code = session.ToString("N")[..8] });
            return 0;
        });
    }

    private Task SetCapabilityAsync(Guid tenant, string feature, bool enabled) =>
        ExecuteSystemAsync("""
            insert into tenant_capabilities (tenant_id, type, key, entitled, enabled)
            values (@tenant, 'feature', @feature, true, @enabled)
            on conflict (tenant_id, type, key) do update set enabled = excluded.enabled
            """, ("tenant", tenant), ("feature", feature), ("enabled", enabled));

    private Task<long> JobIdForFeatureAsync(Guid tenant, string feature) => ScalarSystemAsync<long>("""
        select id from jobs
        where tenant_id = @tenant and kind = 'event' and payload->>'feature' = @feature
        """, ("tenant", tenant), ("feature", feature));

    private async Task<T> ScalarSystemAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_system"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteSystemAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionStringForRole("quesshi_system"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class CollisionFeature : IFeature
    {
        public string Id => "tenant-collision";
        public FeatureSettings Settings { get; } = FeatureSettings.Create(new CollisionSettings(), _ => []);

        public void Configure(IFeatureBuilder feature) =>
            feature.On<SessionEnded>(static (_, _, _) => Task.CompletedTask);

        private sealed record CollisionSettings;
    }
}
