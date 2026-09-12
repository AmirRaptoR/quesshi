using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.Extensions.Logging.Abstractions;
using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Infrastructure.Mongo;
using Quesshi.Server.Seed;

namespace Quesshi.Server.Tests;

public sealed class MatchingMongoTests
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

    [Fact]
    public void Matching_question_document_round_trips_both_answer_sources_and_all_store_fields()
    {
        var created = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var updated = created.AddHours(4);
        var participants = MatchingQuestion.Restore("p", Language.Nl, "m-friends", "Who?",
            MatchingAnswerSource.Participants, null, new MediaRef(MediaKind.Image, "https://x/p", "credit"),
            QuestionStatus.Approved, QuestionSource.Ai, "people/who", created, updated, 7);
        var fixedQuestion = MatchingQuestion.Restore("f", Language.En, "m-friends", "Pick",
            MatchingAnswerSource.Fixed, [" first ", "second"], new MediaRef(MediaKind.Audio, "u"),
            QuestionStatus.Rejected, QuestionSource.Admin, null, created, updated, 3);

        var restoredParticipants = MatchingQuestionDoc.From(participants).ToDomain();
        var restoredFixed = MatchingQuestionDoc.From(fixedQuestion).ToDomain();

        Assert.Empty(restoredParticipants.FixedChoices);
        Assert.Equal(participants.Media, restoredParticipants.Media);
        Assert.Equal(participants.CreatedAt, restoredParticipants.CreatedAt);
        Assert.Equal(participants.UpdatedAt, restoredParticipants.UpdatedAt);
        Assert.Equal(participants.TimesServed, restoredParticipants.TimesServed);
        Assert.Equal(fixedQuestion.FixedChoices, restoredFixed.FixedChoices);
        Assert.Equal(fixedQuestion.Status, restoredFixed.Status);
        Assert.Equal(fixedQuestion.Source, restoredFixed.Source);
    }

    [Fact]
    public async Task Mongo_matching_store_isolated_topics_indexes_filters_and_sampling()
    {
        var client = await TryConnectAsync();
        if (client is null) return;

        var dbName = $"quesshi_test_{Guid.NewGuid():N}";
        try
        {
            var context = new MongoContext(new MongoOptions { ConnectionString = ConnectionString, Database = dbName });
            await context.EnsureIndexesAsync();
            var repository = new MongoMatchingQuestionRepository(context);
            var now = DateTimeOffset.UtcNow;
            var first = MatchingQuestion.Create("m1", Language.En, "m-friends", "Pick a friend",
                MatchingAnswerSource.Fixed, ["A", "B"], now, topic: "people/friends", status: QuestionStatus.Approved);
            var nullTopic = MatchingQuestion.Create("m2", Language.En, "m-friends", "Other",
                MatchingAnswerSource.Participants, null, now, status: QuestionStatus.Approved);
            await repository.UpsertAsync(first);
            await repository.UpsertAsync(nullTopic);

            var duplicate = MatchingQuestion.Create("m3", Language.En, "m-friends", "Duplicate",
                MatchingAnswerSource.Participants, null, now, topic: "people/friends");
            await Assert.ThrowsAsync<MongoWriteException>(() => repository.UpsertAsync(duplicate));

            var topics = await repository.ExistingTopicsAsync(Language.En);
            Assert.Equal(["people/friends"], topics);
            Assert.Empty(await repository.ExistingTopicsAsync(Language.Fa));
            Assert.Single(await repository.SampleApprovedAsync(Language.En, "m-friends", 10, ["m1"]),
                q => q.Id == "m2");
            Assert.Equal(2, await repository.CountAsync(new MatchingQuestionFilter(CategoryId: "m-friends")));
            Assert.Single(await repository.FindAsync(new MatchingQuestionFilter(Lang: Language.En, Text: "friend")),
                q => q.Id == "m1");

            var trivia = Question.Create("same-topic-trivia", Language.En, "general", Difficulty.Easy,
                "Trivia", ["a", "b", "c", "d"], 0, now, topic: "people/trivia");
            await new MongoQuestionRepository(context).UpsertAsync(trivia);
            Assert.DoesNotContain("people/trivia", await repository.ExistingTopicsAsync(Language.En));
        }
        finally
        {
            await client.DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task Seeder_inserts_matching_categories_without_overwriting_an_admin_rename()
    {
        var root = Path.Combine(Path.GetTempPath(), "quesshi-seed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Seed"));
        try
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Seed", "matching_categories.json"),
                Path.Combine(root, "Seed", "matching_categories.json"));
            File.WriteAllText(Path.Combine(root, "Seed", "categories.json"), "[]");
            var matching = new FakeMatchingCategories();
            var questions = new FakeQuestions();
            var categories = new FakeCategories();
            var seeder = new Seeder(questions, categories, new TimeProviderClock(TimeProvider.System),
                NullLogger<Seeder>.Instance, matching);

            await seeder.RunAsync(root);
            Assert.Equal(6, matching.Items.Count);
            Assert.All(matching.Items, c => Assert.StartsWith("m-", c.Id));
            Assert.All(matching.Items, c =>
            {
                Assert.False(string.IsNullOrWhiteSpace(c.NameFa));
                Assert.False(string.IsNullOrWhiteSpace(c.NameEn));
                Assert.False(string.IsNullOrWhiteSpace(c.NameNl));
            });
            await matching.UpsertAsync(matching.Items[0] with { NameEn = "Renamed" });
            await seeder.RunAsync(root);
            Assert.Equal("Renamed", (await matching.GetAsync("m-partners"))!.NameEn);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

}
