using MongoDB.Bson;
using MongoDB.Driver;
using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Infrastructure.Mongo;

namespace Quesshi.Server.Tests;

/// <summary>
/// The one part of #72 that a <see cref="QuestionDoc"/> built in code cannot prove: what a document
/// genuinely written before <c>QuestionKind</c> existed deserialises to. A <c>QuestionDoc</c> we
/// construct always has its <c>Kind</c> property assigned — even "leave it at the default" is an
/// assignment — so it can never exercise the BSON-field-truly-absent path that a pre-migration row
/// actually has. Only a raw <see cref="BsonDocument"/> with no <c>Kind</c> element, inserted straight
/// into the collection, does that. Same reasoning and the same connection pattern as
/// <see cref="MongoMatchArchiveIndexTests"/> — see its remarks for why this needs a real server and
/// how <c>QUESSHI_TEST_MONGO</c> gates it.
/// </summary>
public class MongoQuestionKindTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("QUESSHI_TEST_MONGO") ?? "mongodb://127.0.0.1:27017";

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
        catch (Exception ex) when (Environment.GetEnvironmentVariable("QUESSHI_TEST_MONGO") is null)
        {
            _ = ex;
            return null;
        }
    }

    /// <summary>The exact shape a row written before this migration has: no Kind, no target fields, no base layer.</summary>
    private static BsonDocument LegacyChoiceDoc(string id, string categoryId, QuestionStatus status) => new()
    {
        ["_id"] = id,
        ["Lang"] = (int)Language.En,
        ["CategoryId"] = categoryId,
        ["Level"] = (int)Difficulty.Easy,
        ["Prompt"] = $"Legacy prompt {id}?",
        ["Choices"] = new BsonArray { "a", "b", "c", "d" },
        ["CorrectIndex"] = 1,
        ["MediaKind"] = 0,
        ["MediaUrl"] = "",
        ["MediaAttribution"] = BsonNull.Value,
        ["Explanation"] = BsonNull.Value,
        ["Topic"] = BsonNull.Value,
        ["Status"] = (int)status,
        ["Source"] = (int)QuestionSource.Seed,
        ["CreatedAt"] = DateTime.UtcNow,
        ["TimesServed"] = 0,
        ["TimesCorrect"] = 0,
        ["Reports"] = new BsonArray(),
        ["ReportCount"] = 0
        // Deliberately no Kind, TargetShape, TargetCountryCode, TargetLatitude, TargetLongitude,
        // TargetRadiusKm or BaseLayer -- those elements did not exist when this row was written.
    };

    [Fact]
    public async Task A_legacy_document_with_no_Kind_field_reads_back_as_Choice()
    {
        var client = await TryConnectAsync();
        if (client is null) return; // no declared Mongo in this environment -- see TryConnectAsync's remarks

        var dbName = $"quesshi_test_{Guid.NewGuid():N}";
        try
        {
            var context = new MongoContext(new MongoOptions { ConnectionString = ConnectionString, Database = dbName });
            var raw = client.GetDatabase(dbName).GetCollection<BsonDocument>("questions");
            await raw.InsertOneAsync(LegacyChoiceDoc("legacy-1", "geography", QuestionStatus.Approved));

            var repo = new MongoQuestionRepository(context);
            var question = await repo.GetAsync("legacy-1");

            Assert.NotNull(question);
            Assert.Equal(QuestionKind.Choice, question!.Kind);
            Assert.Null(question.Target);
            Assert.Null(question.BaseLayer);
        }
        finally
        {
            await client.DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task BucketCountsAsync_puts_legacy_documents_in_the_Choice_bucket_and_a_Sort_document_in_its_own()
    {
        var client = await TryConnectAsync();
        if (client is null) return; // no declared Mongo in this environment -- see TryConnectAsync's remarks

        var dbName = $"quesshi_test_{Guid.NewGuid():N}";
        try
        {
            var context = new MongoContext(new MongoOptions { ConnectionString = ConnectionString, Database = dbName });
            var raw = client.GetDatabase(dbName).GetCollection<BsonDocument>("questions");

            // Three genuinely legacy rows, no Kind field at all -- standing in for the 3067 real
            // Choice questions the spec worries a null bucket would swallow. Two approved, one
            // pending, so the counts below have to actually match rather than merely be non-zero.
            await raw.InsertManyAsync(
            [
                LegacyChoiceDoc("legacy-1", "geography", QuestionStatus.Approved),
                LegacyChoiceDoc("legacy-2", "geography", QuestionStatus.Approved),
                LegacyChoiceDoc("legacy-3", "geography", QuestionStatus.Pending)
            ]);

            // One genuine Sort question, same language/category/level, written the normal way.
            // If the Kind coalesce in BucketCountsAsync's projection were missing or wrong, this
            // would either disappear into a null bucket or, worse, merge into the Choice count above.
            var sortQuestion = Question.Create("sort-1", Language.En, "geography", Difficulty.Easy,
                "Order these by population.", ["a", "b", "c", "d"], 0, DateTimeOffset.UtcNow,
                kind: QuestionKind.Sort, status: QuestionStatus.Approved);
            var repo = new MongoQuestionRepository(context);
            await repo.UpsertAsync(sortQuestion);

            var buckets = await repo.BucketCountsAsync();

            var choiceBucket = Assert.Single(buckets, b =>
                b.Lang == Language.En && b.CategoryId == "geography" && b.Level == Difficulty.Easy && b.Kind == QuestionKind.Choice);
            Assert.Equal(2, choiceBucket.Approved);
            Assert.Equal(1, choiceBucket.Pending);

            var sortBucket = Assert.Single(buckets, b =>
                b.Lang == Language.En && b.CategoryId == "geography" && b.Level == Difficulty.Easy && b.Kind == QuestionKind.Sort);
            Assert.Equal(1, sortBucket.Approved);
            Assert.Equal(0, sortBucket.Pending);
        }
        finally
        {
            await client.DropDatabaseAsync(dbName);
        }
    }
}
