using System.Security.Cryptography;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Quesshi.Engine.Contracts;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Streaming;
using Quesshi.Plugin.Api;
using Quesshi.Server;
using Quesshi.Server.Security;
using Quesshi.Server.Streaming;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

[Collection("PostgreSQL")]
public sealed class PlayerBanStreamDatabaseTests(PostgresFixture fixture)
{
    [SkippableFact]
    public async Task Ban_manager_notifies_the_postgres_listener_to_close_the_active_stream()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var sources = CreateSources();
        var listener = new PostgresStreamNotificationListener(sources, NullLogger<PostgresStreamNotificationListener>.Instance);
        var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Received += notification =>
        {
            if (notification is StreamNotification.Reconnected) listening.TrySetResult();
        };
        await listener.StartAsync(CancellationToken.None);
        await listening.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var hub = new StreamHub(null!, null!, null!, new TenantDb(sources), listener, new TestClock(DateTimeOffset.UnixEpoch),
            NullLogger<StreamHub>.Instance, new StreamSessionLiveness(sources));
        try
        {
            var stream = hub.Subscribe(seeded.TenantId, seeded.PlayerId.ToString(), null);
            var manager = new PlayerBanManager(new TenantDb(sources), new TenantLocks(), new NoopRevocationNotifier());

            Assert.Equal(PlayerBanChangeStatus.Success, (await manager.BanAsync(seeded.TenantId, seeded.PlayerId)).Status);
            var closed = await ReadAsync(stream);
            Assert.Equal((StreamMessageKind.Closed, "banned"), (closed.Kind, closed.Reason));
        }
        finally
        {
            hub.Dispose();
            await listener.StopAsync(CancellationToken.None);
            listener.Dispose();
        }
    }

    [SkippableFact]
    public async Task Reconciliation_closes_a_banned_player_but_keeps_live_players_in_each_tenant()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var sources = CreateSources();
        var hub = Hub(sources);
        try
        {
            var banned = hub.Subscribe(seeded.TenantId, seeded.PlayerId.ToString(), null);
            var live = hub.Subscribe(seeded.TenantId, seeded.LivePlayerId.ToString(), null);
            var otherTenant = hub.Subscribe(seeded.OtherTenantId, seeded.OtherPlayerId.ToString(), null);
            await using var owner = await OpenAsync("quesshi_owner");
            await owner.ExecuteAsync("update players set banned_at = now() where tenant_id = @tenantId and id = @playerId",
                new { tenantId = seeded.TenantId, playerId = seeded.PlayerId });

            await hub.ReconcileAsync();

            var closed = await ReadAsync(banned);
            Assert.Equal((StreamMessageKind.Closed, "banned"), (closed.Kind, closed.Reason));
            Assert.False(live.Messages.TryRead(out _));
            Assert.False(otherTenant.Messages.TryRead(out _));
        }
        finally
        {
            hub.Dispose();
        }
    }

    [SkippableFact]
    public async Task Reconciliation_closes_a_deleted_player_when_the_notification_is_lost()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var sources = CreateSources();
        var hub = Hub(sources);
        try
        {
            var stream = hub.Subscribe(seeded.TenantId, seeded.PlayerId.ToString(), null);
            await using var owner = await OpenAsync("quesshi_owner");
            await owner.ExecuteAsync("update players set deleted_at = now() where tenant_id = @tenantId and id = @playerId",
                new { tenantId = seeded.TenantId, playerId = seeded.PlayerId });

            await hub.ReconcileAsync();

            var closed = await ReadAsync(stream);
            Assert.Equal((StreamMessageKind.Closed, "deleted"), (closed.Kind, closed.Reason));
        }
        finally
        {
            hub.Dispose();
        }
    }

    [SkippableFact]
    public async Task Reconciliation_closes_a_revoked_session_after_a_ban_is_undone()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        await using var sources = CreateSources();
        var sessionKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using (var system = await OpenAsync("quesshi_system"))
        {
            await system.ExecuteAsync("""
                insert into auth_sessions (token_hash, tenant_id, account_id, expires_at)
                values (@tokenHash, @tenantId, @accountId, now() + interval '30 days')
                """, new { tokenHash = Convert.FromHexString(sessionKey), seeded.TenantId, seeded.AccountId });
        }
        var hub = Hub(sources);
        try
        {
            var stream = hub.Subscribe(seeded.TenantId, seeded.PlayerId.ToString(), null, sessionKey);
            var manager = new PlayerBanManager(new TenantDb(sources), new TenantLocks(), new NoopRevocationNotifier());
            Assert.Equal(PlayerBanChangeStatus.Success, (await manager.BanAsync(seeded.TenantId, seeded.PlayerId)).Status);
            Assert.Equal(PlayerBanChangeStatus.Success, (await manager.UnbanAsync(seeded.TenantId, seeded.PlayerId)).Status);

            await hub.ReconcileAsync();

            var closed = await ReadAsync(stream);
            Assert.Equal((StreamMessageKind.Closed, "signed_out"), (closed.Kind, closed.Reason));
        }
        finally
        {
            hub.Dispose();
        }
    }

    private async Task<(Guid TenantId, Guid OtherTenantId, Guid AccountId, Guid PlayerId, Guid LivePlayerId, Guid OtherPlayerId)> SeedAsync()
    {
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var playerId = Guid.NewGuid();
        var livePlayerId = Guid.NewGuid();
        var otherPlayerId = Guid.NewGuid();
        await using var owner = await OpenAsync("quesshi_owner");
        await owner.ExecuteAsync("""
            insert into tenants (id, slug, name, default_language)
            values (@tenantId, @tenantSlug, 'Stream tenant', 'en'), (@otherTenantId, @otherTenantSlug, 'Other stream tenant', 'en');
            insert into accounts (id, email) values (@accountId, @email);
            insert into players (tenant_id, id, account_id, display_name, avatar_seed, lang)
            values (@tenantId, @playerId, @accountId, 'Target', 'target', 'en'),
                   (@tenantId, @livePlayerId, null, 'Live', 'live', 'en'),
                   (@otherTenantId, @otherPlayerId, null, 'Other', 'other', 'en');
            """, new
        {
            tenantId,
            otherTenantId,
            accountId,
            playerId,
            livePlayerId,
            otherPlayerId,
            tenantSlug = $"stream-{tenantId:N}",
            otherTenantSlug = $"stream-other-{otherTenantId:N}",
            email = $"stream-{accountId:N}@example.test"
        });
        return (tenantId, otherTenantId, accountId, playerId, livePlayerId, otherPlayerId);
    }

    private DatabaseSources CreateSources() => new(fixture.ConnectionStringForRole("quesshi_app"), fixture.ConnectionStringForRole("quesshi_system"));

    private StreamHub Hub(DatabaseSources sources) => new(null!, null!, null!, new TenantDb(sources), new SilentNotifications(),
        new TestClock(DateTimeOffset.UnixEpoch), NullLogger<StreamHub>.Instance, new StreamSessionLiveness(sources));

    private async Task<NpgsqlConnection> OpenAsync(string role)
    {
        var connection = new NpgsqlConnection(fixture.ConnectionStringForRole(role));
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<StreamMessage> ReadAsync(StreamSubscription subscription) =>
        await subscription.Messages.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

    private sealed class SilentNotifications : IStreamNotificationSource
    {
        public event Action<StreamNotification>? Received
        {
            add { }
            remove { }
        }
    }

    private sealed class NoopRevocationNotifier : IPlayerAccessRevocationNotifier
    {
        public void RevokePlayer(Guid tenantId, Guid playerId) { }
    }
}
