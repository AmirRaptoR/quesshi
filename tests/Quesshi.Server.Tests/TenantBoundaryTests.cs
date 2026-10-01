using System.IdentityModel.Tokens.Jwt;
using MongoDB.Bson;
using MongoDB.Driver;
using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Infrastructure;
using Quesshi.Infrastructure.Mongo;
using Quesshi.Grains.Abstractions;
using Quesshi.Server.Auth;
using Orleans.Runtime;

namespace Quesshi.Server.Tests;

public sealed class TenantBoundaryTests
{
    private static readonly string MongoConnectionString =
        Environment.GetEnvironmentVariable("QUESSHI_TEST_MONGO") ?? "mongodb://127.0.0.1:27017";

    [Fact]
    public void Mongo_database_keeps_legacy_quesshi_name_and_uses_stable_tenant_ids_for_others()
    {
        var tenant = new TenantContext();
        var mongo = new MongoContext(new MongoOptions { Database = "game" }, tenant);

        Assert.Equal("game", mongo.DatabaseName);
        using (tenant.Enter("quessher")) Assert.Equal("game_quessher", mongo.DatabaseName);
        using (tenant.Enter("brand-c")) Assert.Equal("game_brand-c", mongo.DatabaseName);
        Assert.Equal("game", mongo.DatabaseName);
    }

    [Fact]
    public void Redis_keys_preserve_quesshi_names_and_namespace_other_tenants()
    {
        var tenant = new TenantContext();

        Assert.Equal("quesshi:otp:a@example.test", tenant.Key("quesshi:otp:a@example.test"));
        using (tenant.Enter("quessher"))
            Assert.Equal("quesshi:quessher:otp:a@example.test", tenant.Key("quesshi:otp:a@example.test"));
    }

    [Fact]
    public void Player_and_admin_tokens_carry_the_current_tenant_id()
    {
        var tenant = new TenantContext();
        using (tenant.Enter("quessher"))
        {
            var playerToken = new TokenIssuer(new JwtOptions { Key = "a-player-signing-key-long-enough", Issuer = "quesshi" }, tenant)
                .Issue(Player.Guest("p1", "Guest", Language.En, DateTimeOffset.UtcNow));
            var playerClaims = new JwtSecurityTokenHandler().ReadJwtToken(playerToken).Claims;
            Assert.Equal("quessher", playerClaims.Single(c => c.Type == TokenIssuer.TenantClaim).Value);

            var adminToken = new AdminTokenIssuer(new AdminAuthOptions { Key = "an-admin-signing-key-long-enough", Issuer = "quesshi" }, tenant)
                .Issue(AdminUser.Create("a1", "admin", "admin@example.test", "hash", DateTimeOffset.UtcNow));
            var adminClaims = new JwtSecurityTokenHandler().ReadJwtToken(adminToken).Claims;
            Assert.Equal("quessher", adminClaims.Single(c => c.Type == TokenIssuer.TenantClaim).Value);
        }
    }

    [Fact]
    public void Grain_addresses_preserve_quesshi_keys_and_separate_tenant_keys_stably()
    {
        Assert.Equal("same-id", TenantGrainAddress.StringKey("same-id", "quesshi"));
        Assert.Null(TenantGrainAddress.IntegerKeyExtension("quesshi"));
        Assert.Equal("brand-a", TenantGrainAddress.IntegerKeyExtension("brand-a"));
        Assert.Equal("brand-b", TenantGrainAddress.IntegerKeyExtension("brand-b"));
        Assert.NotEqual(TenantGrainAddress.StringKey("same-id", "brand-a"),
            TenantGrainAddress.StringKey("same-id", "brand-b"));
        Assert.Equal("brand-a", TenantGrainAddress.TenantId("brand-a:same-id"));
        Assert.Equal("quesshi", TenantGrainAddress.TenantId("same-id"));
        Assert.Equal("brand-a", TenantGrainAddress.TenantId(GrainId.Create(GrainType.Create("integer"), GrainIdKeyExtensions.CreateIntegerKey(42, "brand-a"))));
        Assert.Equal("quesshi", TenantGrainAddress.TenantId(GrainId.Create(GrainType.Create("integer"), GrainIdKeyExtensions.CreateIntegerKey(42))));
        Assert.Equal(GrainIdKeyExtensions.CreateIntegerKey(42), GrainIdKeyExtensions.CreateIntegerKey(42, string.Empty));
    }

    [Fact]
    public async Task Mongo_player_reads_and_writes_are_isolated_while_legacy_database_remains_the_default()
    {
        var mongoSettings = MongoClientSettings.FromConnectionString(MongoConnectionString);
        mongoSettings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);
        var mongoClient = new MongoClient(mongoSettings);
        try
        {
            await mongoClient.GetDatabase("admin").RunCommandAsync((Command<BsonDocument>)"{ ping: 1 }");
        }
        catch (Exception) when (Environment.GetEnvironmentVariable("QUESSHI_TEST_MONGO") is null)
        {
            return;
        }

        var database = $"quesshi_tenant_test_{Guid.NewGuid():N}";
        await mongoClient.GetDatabase(database).GetCollection<BsonDocument>("players").InsertOneAsync(new BsonDocument
        {
            ["_id"] = "same-id",
            ["Email"] = "legacy@example.test",
            ["DisplayName"] = "Legacy",
            ["AvatarSeed"] = "legacy-seed",
            ["Lang"] = (int)Language.En,
            ["IsBanned"] = false,
            ["IsGuest"] = false,
            ["CreatedAt"] = DateTime.UtcNow,
            ["Wins"] = 4,
            ["Losses"] = 2,
            ["Draws"] = 1,
            ["Streak"] = 2,
            ["BestStreak"] = 3,
            ["TotalScore"] = 120,
            ["ByCategory"] = new BsonDocument(),
            ["Friends"] = new BsonArray()
        });
        var tenant = new TenantContext();
        var context = new MongoContext(new MongoOptions { ConnectionString = MongoConnectionString, Database = database }, tenant);
        var players = new MongoPlayerRepository(context);
        var archive = new MongoMatchArchive(context);
        try
        {
            var legacy = await players.GetAsync("same-id");
            Assert.Equal("Legacy", legacy?.DisplayName);
            Assert.Equal(4, legacy?.Stats.Wins);

            var legacyMatch = new ArchivedMatch("same-match", "SHARED1", Language.En, "same-id", null, "same-id", false,
                [new ParticipantResult("same-id", 15, 1, MatchOutcome.Win)], MatchState.Resolved,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ["q1"]);
            await archive.SaveAsync(legacyMatch);
            Assert.Equal("same-id", (await archive.ByCodeAsync("shared1"))?.ChallengerId);

            using (tenant.Enter("brand-a"))
            {
                Assert.Null(await players.GetAsync("same-id"));
                Assert.Null(await archive.ByCodeAsync("shared1"));
                await archive.SaveAsync(legacyMatch with { ChallengerId = "brand-a-player" });
                await players.UpsertAsync(Player.Register("same-id", "brand-a@example.test", "Brand A", Language.Nl, DateTimeOffset.UtcNow));
                Assert.Equal("Brand A", (await players.GetAsync("same-id"))?.DisplayName);
                Assert.Equal("brand-a-player", (await archive.ByCodeAsync("shared1"))?.ChallengerId);
            }

            Assert.Equal("Legacy", (await players.GetAsync("same-id"))?.DisplayName);
            Assert.Equal("same-id", (await archive.ByCodeAsync("shared1"))?.ChallengerId);
            using (tenant.Enter("brand-a"))
                Assert.Equal("Brand A", (await players.GetAsync("same-id"))?.DisplayName);
        }
        finally
        {
            await mongoClient.DropDatabaseAsync(database);
            await mongoClient.DropDatabaseAsync($"{database}_brand-a");
        }
    }
}
