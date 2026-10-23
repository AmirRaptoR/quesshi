using System.Text.Json;
using Npgsql;
using Quesshi.Application;
using Quesshi.Application.Jobs;
using Quesshi.Features.Features.Friends;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Jobs;
using Quesshi.Plugin.Api;
using Quesshi.Server;
using Quesshi.Server.Accounts;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

public sealed class FriendshipDatabaseTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task Friendship_rows_are_isolated_to_the_current_tenant_for_the_application_role()
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();

        await using var connection = await OpenAsync("quesshi_app");
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, "select set_config('app.tenant_id', cast(@tenantId as text), true)",
            ("tenantId", seeded.TenantId));
        var visiblePlayerIds = await ReadGuidsAsync(connection, transaction, "select player_id from friendships order by player_id");

        Assert.Equivalent(new[] { seeded.PlayerId, seeded.FriendId }, visiblePlayerIds);
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Core_player_cleanup_removes_friendships_for_either_participant(bool cleanFirstPlayer)
    {
        Skip.If(fixture.ConnectionString is null, "PostgreSQL is not configured.");
        var seeded = await SeedAsync();
        var playerId = cleanFirstPlayer ? seeded.PlayerId : seeded.FriendId;
        await using var sources = new DatabaseSources(
            fixture.ConnectionStringForRole("quesshi_app"),
            fixture.ConnectionStringForRole("quesshi_system"));
        var registry = FeatureRegistry.Create([new FriendsFeature()]);
        var effects = new DurableEvents(registry, new TenantJobs(), new TenantLocks(), new NullSessionEffectsNotifier());
        var cleanup = new CorePlayerCleanup(new TenantLocks(), effects);
        registry.Jobs.Register("core", "cleanup-player", effects.HandleCleanupJobAsync);

        await cleanup.HandleAsync(new JobContext(0, seeded.TenantId, "core", "player-cleanup",
            JsonSerializer.SerializeToElement(new PlayerCleanupPayload(playerId)), new TenantDb(sources)), CancellationToken.None);
        var worker = new JobWorker(new PostgresJobStore(sources), registry.Jobs, new TenantDb(sources), new OffCapabilities());
        await worker.RunPassAsync(1, TimeSpan.FromMinutes(1));

        Assert.Equal(0L, await TenantScalarAsync<long>(seeded.TenantId,
            "select count(*) from friendships where player_id = @playerId or friend_id = @playerId", new { playerId }));
    }

    private async Task<(Guid TenantId, Guid PlayerId, Guid FriendId)> SeedAsync()
    {
        await Program.MigrateAsync(fixture.ConnectionStringForRole("quesshi_owner"));
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var playerId = Guid.NewGuid();
        var friendId = Guid.NewGuid();
        var otherPlayerId = Guid.NewGuid();
        var otherFriendId = Guid.NewGuid();
        await using var connection = await OpenAsync();
        await ExecuteAsync(connection, null, """
            insert into tenants (id, slug, name, default_language)
            values (@tenantId, @tenantSlug, 'Friends', 'en'),
                   (@otherTenantId, @otherTenantSlug, 'Other friends', 'en');
            insert into tenant_languages (tenant_id, code, name, english_name)
            values (@tenantId, 'en', 'English', 'English'),
                   (@otherTenantId, 'en', 'English', 'English');
            insert into players (tenant_id, id, display_name, avatar_seed, lang)
            values (@tenantId, @playerId, 'Player', 'player', 'en'),
                   (@tenantId, @friendId, 'Friend', 'friend', 'en'),
                   (@otherTenantId, @otherPlayerId, 'Other player', 'other-player', 'en'),
                   (@otherTenantId, @otherFriendId, 'Other friend', 'other-friend', 'en');
            insert into friendships (tenant_id, player_id, friend_id)
            values (@tenantId, @playerId, @friendId),
                   (@tenantId, @friendId, @playerId),
                   (@otherTenantId, @otherPlayerId, @otherFriendId),
                   (@otherTenantId, @otherFriendId, @otherPlayerId);
            """,
            ("tenantId", tenantId), ("tenantSlug", $"friends-{tenantId:N}"),
            ("otherTenantId", otherTenantId), ("otherTenantSlug", $"other-friends-{otherTenantId:N}"),
            ("playerId", playerId), ("friendId", friendId),
            ("otherPlayerId", otherPlayerId), ("otherFriendId", otherFriendId));
        return (tenantId, playerId, friendId);
    }

    private async Task<T> TenantScalarAsync<T>(Guid tenantId, string sql, object parameters)
    {
        await using var sources = new DatabaseSources(
            fixture.ConnectionStringForRole("quesshi_app"),
            fixture.ConnectionStringForRole("quesshi_system"));
        return (await new TenantDb(sources).InTenantAsync(tenantId,
            transaction => transaction.QuerySingleOrDefaultAsync<T>(sql, parameters, CancellationToken.None), readOnly: true))!;
    }

    private async Task<NpgsqlConnection> OpenAsync(string role = "quesshi_owner")
    {
        var connection = new NpgsqlConnection(fixture.ConnectionStringForRole(role));
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlyList<Guid>> ReadGuidsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<Guid>();
        while (await reader.ReadAsync()) values.Add(reader.GetGuid(0));
        return values;
    }

    private sealed class OffCapabilities : ICapabilities
    {
        public Task<bool> IsOnAsync(Guid tenantId, CapabilityType type, string key, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<TSettings> GetSettingsAsync<TSettings>(Guid tenantId, CapabilityType type, string key, CancellationToken cancellationToken = default)
            where TSettings : notnull => throw new NotSupportedException();

        public Task SetAsync<TSettings>(Guid tenantId, CapabilityType type, string key, bool enabled, TSettings settings, CancellationToken cancellationToken = default)
            where TSettings : notnull => throw new NotSupportedException();
    }
}
