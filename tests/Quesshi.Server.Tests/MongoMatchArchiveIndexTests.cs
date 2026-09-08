using MongoDB.Bson;
using MongoDB.Driver;
using Quesshi.Infrastructure.Mongo;

namespace Quesshi.Server.Tests;

/// <summary>
/// The one part of this migration that genuinely needs a real MongoDB — an index is a database-level
/// mechanism, and a fake <c>IMatchArchive</c> (used by every other test in this project) has no notion
/// of one, so it cannot prove that swapping the two compound indexes for a single multikey index leaves
/// <c>ForPlayerAsync</c> answering the same question it always did. This talks to the same server the
/// running application uses (default <c>mongodb://127.0.0.1:27017</c>, overridable with
/// <c>QUESSHI_TEST_MONGO</c>), in a throwaway database it drops when it is done.
///
/// The two legacy documents below are inserted as raw <see cref="BsonDocument"/>s — never through
/// <see cref="MatchDoc"/> — so they carry exactly the old shape: no <c>OwnerId</c>, no
/// <c>Participants</c>, no <c>Results</c>. That is what a row written before this migration actually
/// looks like in the collection, and it is <see cref="MongoContext.EnsureIndexesAsync"/>'s own
/// one-time backfill — not a test setup shortcut — that has to bring them up to date before the new
/// index and its <c>AnyEq</c> query can serve them at all.
/// </summary>
public class MongoMatchArchiveIndexTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("QUESSHI_TEST_MONGO") ?? "mongodb://127.0.0.1:27017";

    /// <summary>
    /// A best-effort integration test, not a skip: there is no live-infrastructure marker in this
    /// project's test framework (xUnit v2 has no runtime <c>Skip.If</c>), and CI does not run a Mongo
    /// container the way the compose-based dev environment does. A short server-selection timeout
    /// means an environment with no reachable Mongo returns quickly and quietly rather than hanging or
    /// failing the whole suite; wherever Mongo *is* reachable — including this repository's own dev
    /// stack — the real assertions below run for real.
    /// </summary>
    private static async Task<MongoClient?> TryConnectAsync()
    {
        try
        {
            var settings = MongoClientSettings.FromConnectionString(ConnectionString);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);
            var client = new MongoClient(settings);
            await client.GetDatabase("admin").RunCommandAsync((Command<BsonDocument>)"{ ping: 1 }");
            return client;
        }
        catch
        {
            return null;
        }
    }

    [Fact]
    public async Task The_multikey_Participants_index_serves_ForPlayerAsync_the_same_rows_the_two_indexes_it_replaces_did()
    {
        var client = await TryConnectAsync();
        if (client is null) return; // no reachable Mongo in this environment -- see TryConnectAsync's remarks

        var dbName = $"quesshi_test_{Guid.NewGuid():N}";
        var db = client.GetDatabase(dbName);
        try
        {
            var raw = db.GetCollection<BsonDocument>("matches");

            // Two genuinely legacy rows -- the exact shape a document written before this migration
            // has: ChallengerId/OpponentId and nothing else naming a participant.
            await raw.InsertManyAsync(
            [
                new BsonDocument
                {
                    ["_id"] = "legacy-both", ["Code"] = "LEGACY1", ["Lang"] = 0,
                    ["ChallengerId"] = "p-challenger", ["OpponentId"] = "p-opponent",
                    ["WinnerId"] = BsonNull.Value, ["IsDraw"] = false,
                    ["ChallengerScore"] = 10, ["OpponentScore"] = 4,
                    ["State"] = (int)Quesshi.Domain.MatchState.InProgress,
                    ["CreatedAt"] = DateTime.UtcNow, ["EndedAt"] = BsonNull.Value,
                    ["QuestionIds"] = new BsonArray(), ["IsLive"] = false
                },
                new BsonDocument
                {
                    // A legacy lobby nobody had joined: null OpponentId, exactly the case the backfill
                    // formula must not turn into a phantom second participant.
                    ["_id"] = "legacy-lobby", ["Code"] = "LEGACY2", ["Lang"] = 0,
                    ["ChallengerId"] = "p-lobby-owner", ["OpponentId"] = BsonNull.Value,
                    ["WinnerId"] = BsonNull.Value, ["IsDraw"] = false,
                    ["ChallengerScore"] = 0, ["OpponentScore"] = 0,
                    ["State"] = (int)Quesshi.Domain.MatchState.AwaitingOpponent,
                    ["CreatedAt"] = DateTime.UtcNow, ["EndedAt"] = BsonNull.Value,
                    ["QuestionIds"] = new BsonArray(), ["IsLive"] = false
                }
            ]);

            // The two indexes this migration replaces, built exactly as the pre-migration
            // MongoContext.EnsureIndexesAsync built them -- so the drop step below has something
            // real to prove it removes, not just an assumption that it would.
            await raw.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("ChallengerId").Descending("CreatedAt")),
                new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("OpponentId").Descending("CreatedAt"))
            ]);

            // A row already in the new shape, as if this code had written it -- proof the backfill
            // leaves an up-to-date row alone rather than needing every row to be legacy.
            var context = new MongoContext(new MongoOptions { ConnectionString = ConnectionString, Database = dbName });
            var freshDoc = MatchDoc.From(new Quesshi.Application.Ports.ArchivedMatch(
                "fresh-1", "FRESH01", Quesshi.Domain.Language.En, "p-challenger", "p-third", null, false,
                [new Quesshi.Application.Ports.ParticipantResult("p-challenger", 0, 0, Quesshi.Domain.MatchOutcome.Loss),
                 new Quesshi.Application.Ports.ParticipantResult("p-third", 0, 0, Quesshi.Domain.MatchOutcome.Loss)],
                Quesshi.Domain.MatchState.InProgress, DateTimeOffset.UtcNow, null, []));
            await context.Matches.InsertOneAsync(freshDoc);

            // The behaviour under test: EnsureIndexesAsync's backfill plus the multikey index, run
            // against a collection that is a genuine mix of legacy and already-migrated rows.
            await context.EnsureIndexesAsync();

            var archive = new MongoMatchArchive(context);

            foreach (var (playerId, expectedIds) in new[]
            {
                ("p-challenger", new[] { "legacy-both", "fresh-1" }),
                ("p-opponent", new[] { "legacy-both" }),
                ("p-lobby-owner", new[] { "legacy-lobby" }),
                ("p-third", new[] { "fresh-1" }),
                ("p-nobody", Array.Empty<string>())
            })
            {
                var rows = await archive.ForPlayerAsync(playerId, take: 10);
                Assert.Equal(expectedIds.OrderBy(x => x), rows.Select(r => r.Id).OrderBy(x => x));
            }

            // The old two-index approach's own logic, computed independently against the raw
            // collection, has to agree with what the new single-index query above returned — this is
            // the actual "serves the same results" proof, not just a fixed expectation.
            var afterBackfill = await context.Matches.Find(FilterDefinition<MatchDoc>.Empty).ToListAsync();
            foreach (var playerId in new[] { "p-challenger", "p-opponent", "p-lobby-owner", "p-third", "p-nobody" })
            {
                var oldWay = afterBackfill
                    .Where(d => d.ChallengerId == playerId || d.OpponentId == playerId)
                    .Select(d => d.Id).OrderBy(x => x).ToList();
                var newWay = (await archive.ForPlayerAsync(playerId, take: 10)).Select(r => r.Id).OrderBy(x => x).ToList();
                Assert.Equal(oldWay, newWay);
            }

            // The replacement is real, not additive: the two compound indexes are gone, and the
            // multikey one is there in their place.
            var indexNames = (await context.Matches.Indexes.List().ToListAsync())
                .Select(ix => ix["name"].AsString).ToList();
            Assert.DoesNotContain("ChallengerId_1_CreatedAt_-1", indexNames);
            Assert.DoesNotContain("OpponentId_1_CreatedAt_-1", indexNames);
            Assert.Contains("Participants_1_CreatedAt_-1", indexNames);
        }
        finally
        {
            await client.DropDatabaseAsync(dbName);
        }
    }
}
