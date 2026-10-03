using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Infrastructure.Mongo;
using Quesshi.Infrastructure;
using Quesshi.Server.Seed;

namespace Quesshi.Server.Tests;

public sealed class VotingMongoTests
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
    public async Task Content_settings_persist_in_one_document_per_tenant_database()
    {
        var client = await TryConnectAsync();
        if (client is null) return;
        // Keep the tenant-suffixed database names below MongoDB's 63-character limit.
        var database = "qs_cs_" + Guid.NewGuid().ToString("N");
        var options = new MongoOptions { ConnectionString = ConnectionString, Database = database };
        var firstTenant = new TenantContext();
        var secondTenant = new TenantContext();
        try
        {
            using (firstTenant.Enter("tenant-a"))
            {
                var repository = new MongoContentSettingsRepository(new MongoContext(options, firstTenant));
                await repository.SaveAsync(new ContentSettings(["trivia"], ["voting"]));
                var persisted = await repository.GetAsync();
                Assert.Equal(["trivia"], persisted.TriviaCategoryIds);
                Assert.Equal(["voting"], persisted.VotingCategoryIds);
            }
            using (secondTenant.Enter("tenant-b"))
            {
                var repository = new MongoContentSettingsRepository(new MongoContext(options, secondTenant));
                var missing = await repository.GetAsync();
                Assert.Empty(missing.TriviaCategoryIds);
                Assert.Empty(missing.VotingCategoryIds);
            }
            Assert.Equal(1, await client.GetDatabase(database + "_tenant-a")
                .GetCollection<ContentSettingsDoc>("content_settings").CountDocumentsAsync(FilterDefinition<ContentSettingsDoc>.Empty));
            Assert.Equal(0, await client.GetDatabase(database + "_tenant-b")
                .GetCollection<ContentSettingsDoc>("content_settings").CountDocumentsAsync(FilterDefinition<ContentSettingsDoc>.Empty));
        }
        finally
        {
            await client.DropDatabaseAsync(database + "_tenant-a");
            await client.DropDatabaseAsync(database + "_tenant-b");
        }
    }

    [Fact]
    public void Shared_question_document_round_trips_voting_fields_and_rejects_wrong_family()
    {
        var created = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var updated = created.AddHours(4);
        var participants = VotingQuestion.Restore("p", Language.Nl, "m-friends", "Who?",
            VotingAnswerSource.Participants, null, new MediaRef(MediaKind.Image, "https://x/p", "credit"),
            QuestionStatus.Approved, QuestionSource.Ai, "people/who", created, updated, 7);
        var fixedQuestion = VotingQuestion.Restore("f", Language.En, "m-friends", "Pick",
            VotingAnswerSource.Fixed, [" first ", "second"], new MediaRef(MediaKind.Audio, "u"),
            QuestionStatus.Rejected, QuestionSource.Admin, null, created, updated, 3);

        var participantsDoc = QuestionDoc.From(participants);
        var fixedDoc = QuestionDoc.From(fixedQuestion);
        var restoredParticipants = participantsDoc.ToVoting();
        var restoredFixed = fixedDoc.ToVoting();

        Assert.Equal((int)QuestionFamily.Voting, participantsDoc.Family);
        var persistedVoting = participantsDoc.ToBsonDocument();
        Assert.False(persistedVoting.Contains("Level"));
        Assert.False(persistedVoting.Contains("CorrectIndex"));
        Assert.Equal(participants.Id, restoredParticipants.Id);
        Assert.Equal(participants.Lang, restoredParticipants.Lang);
        Assert.Equal(participants.CategoryId, restoredParticipants.CategoryId);
        Assert.Equal(participants.Prompt, restoredParticipants.Prompt);
        Assert.Equal(participants.AnswerSource, restoredParticipants.AnswerSource);
        Assert.Empty(restoredParticipants.FixedChoices);
        Assert.Equal(participants.Media, restoredParticipants.Media);
        Assert.Equal(participants.Topic, restoredParticipants.Topic);
        Assert.Equal(participants.Status, restoredParticipants.Status);
        Assert.Equal(participants.Source, restoredParticipants.Source);
        Assert.Equal(participants.CreatedAt, restoredParticipants.CreatedAt);
        Assert.Equal(participants.UpdatedAt, restoredParticipants.UpdatedAt);
        Assert.Equal(participants.TimesServed, restoredParticipants.TimesServed);
        Assert.Equal(fixedQuestion.Id, restoredFixed.Id);
        Assert.Equal(fixedQuestion.Lang, restoredFixed.Lang);
        Assert.Equal(fixedQuestion.CategoryId, restoredFixed.CategoryId);
        Assert.Equal(fixedQuestion.Prompt, restoredFixed.Prompt);
        Assert.Equal(fixedQuestion.AnswerSource, restoredFixed.AnswerSource);
        Assert.Equal(fixedQuestion.FixedChoices, restoredFixed.FixedChoices);
        Assert.Equal(fixedQuestion.Media, restoredFixed.Media);
        Assert.Equal(fixedQuestion.Topic, restoredFixed.Topic);
        Assert.Equal(fixedQuestion.Status, restoredFixed.Status);
        Assert.Equal(fixedQuestion.Source, restoredFixed.Source);
        Assert.Equal(fixedQuestion.CreatedAt, restoredFixed.CreatedAt);
        Assert.Equal(fixedQuestion.UpdatedAt, restoredFixed.UpdatedAt);
        Assert.Equal(fixedQuestion.TimesServed, restoredFixed.TimesServed);
        Assert.Throws<InvalidOperationException>(() => participantsDoc.ToTrivia());
        Assert.Throws<InvalidOperationException>(() => QuestionDoc.From(
            Question.Create("trivia-choice", Language.En, "geo", Difficulty.Easy, "which?", ["a", "b", "c", "d"], 0, created)).ToVoting());
    }

    [Fact]
    public async Task Ensure_indexes_backfills_legacy_trivia_before_family_scoped_repository_operations()
    {
        var client = await TryConnectAsync();
        if (client is null) return;

        var dbName = $"quesshi_test_{Guid.NewGuid():N}";
        try
        {
            var context = new MongoContext(new MongoOptions { ConnectionString = ConnectionString, Database = dbName });
            await context.EnsureIndexesAsync();
            var legacy = new BsonDocument
            {
                ["_id"] = "legacy-trivia",
                ["Lang"] = (int)Language.En,
                ["CategoryId"] = "legacy-category",
                ["Level"] = (int)Difficulty.Easy,
                ["Prompt"] = "Legacy trivia question",
                ["Choices"] = new BsonArray(["a", "b", "c", "d"]),
                ["CorrectIndex"] = 0,
                ["MediaKind"] = (int)MediaKind.None,
                ["MediaUrl"] = "",
                ["Status"] = (int)QuestionStatus.Approved,
                ["Source"] = (int)QuestionSource.Seed,
                ["CreatedAt"] = DateTime.UtcNow,
                ["TimesServed"] = 0,
                ["TimesCorrect"] = 0,
                ["AnswerSource"] = 0,
                ["FixedChoices"] = new BsonArray(),
                ["UpdatedAt"] = DateTime.UtcNow,
                ["ServedTokens"] = new BsonArray(),
                ["Reports"] = new BsonArray(),
                ["ReportCount"] = 0
            };
            var rawQuestions = client.GetDatabase(dbName).GetCollection<BsonDocument>("questions");
            await rawQuestions.InsertOneAsync(legacy);

            await context.EnsureIndexesAsync();

            var repository = new MongoQuestionRepository(context);
            Assert.Equal((int)QuestionFamily.Trivia,
                (await rawQuestions.Find(new BsonDocument("_id", "legacy-trivia")).SingleAsync())["Family"].AsInt32);
            Assert.Equal("legacy-trivia", (await repository.GetAsync("legacy-trivia"))!.Id);
            Assert.Contains(await repository.SampleApprovedAsync(Language.En, ContentScope.All, Difficulty.Easy, 1, []),
                q => q.Id == "legacy-trivia");

            var edited = Question.Create("legacy-trivia", Language.En, "legacy-category", Difficulty.Easy,
                "Edited legacy trivia", ["a", "b", "c", "d"], 0, DateTimeOffset.UtcNow,
                status: QuestionStatus.Approved);
            await repository.UpsertAsync(edited);
            Assert.Equal("Edited legacy trivia", (await repository.GetAsync("legacy-trivia"))!.Prompt);
            Assert.Equal(1, await repository.CountAsync(new QuestionFilter(Lang: Language.En)));
        }
        finally
        {
            await client.DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public async Task Mongo_voting_store_isolated_topics_indexes_filters_and_sampling()
    {
        var client = await TryConnectAsync();
        if (client is null) return;

        var dbName = $"quesshi_test_{Guid.NewGuid():N}";
        try
        {
            var context = new MongoContext(new MongoOptions { ConnectionString = ConnectionString, Database = dbName });
            await context.EnsureIndexesAsync();
            var questionIndexes = await context.Questions.Indexes.ListAsync();
            var indexNames = (await questionIndexes.ToListAsync()).Select(index => index["name"].AsString).ToArray();
            Assert.Contains("Family_1_Status_1_Lang_1_CategoryId_1_Level_1", indexNames);
            Assert.Contains("OwnerId_1", indexNames);
            Assert.Contains("ReportCount_-1", indexNames);
            Assert.Contains("Lang_1_Family_1_Topic_1", indexNames);
            Assert.DoesNotContain("Lang_1_Topic_1", indexNames);
            Assert.DoesNotContain("CategoryId_1", indexNames);
            Assert.DoesNotContain("Status_1_Lang_1_CategoryId_1_Level_1", indexNames);
            var categories = new MongoCategoryRepository(context);
            await categories.UpsertAsync(new Category("m-friends", "دوستان", "Friends", "people", "blue"));
            var repository = new MongoVotingQuestionRepository(context);
            var now = DateTimeOffset.UtcNow;
            var first = VotingQuestion.Create("m1", Language.En, "m-friends", "Pick a friend",
                VotingAnswerSource.Fixed, ["A", "B"], now, topic: "people/friends", status: QuestionStatus.Approved);
            var nullTopic = VotingQuestion.Create("m2", Language.En, "m-friends", "Other",
                VotingAnswerSource.Participants, null, now, status: QuestionStatus.Approved);
            var secondNullTopic = VotingQuestion.Create("m3", Language.En, "m-friends", "Another",
                VotingAnswerSource.Participants, null, now, status: QuestionStatus.Approved);
            var emptyTopic = VotingQuestion.Create("m4", Language.En, "m-friends", "Empty topic",
                VotingAnswerSource.Participants, null, now, topic: "", status: QuestionStatus.Approved);
            var otherLanguage = VotingQuestion.Create("m5", Language.Fa, "m-friends", "A friend",
                VotingAnswerSource.Fixed, ["الف", "ب"], now, topic: "people/friends", status: QuestionStatus.Approved);
            var pending = VotingQuestion.Create("m6", Language.En, "m-other", "Draft friend",
                VotingAnswerSource.Fixed, ["A", "B"], now, topic: "draft", status: QuestionStatus.Pending);
            var rejected = VotingQuestion.Create("m7", Language.En, "m-friends", "Rejected friend",
                VotingAnswerSource.Fixed, ["A", "B"], now, topic: "rejected", status: QuestionStatus.Rejected);
            await repository.UpsertAsync(first);
            await repository.UpsertAsync(nullTopic);
            await repository.UpsertAsync(secondNullTopic);
            await repository.UpsertAsync(emptyTopic);
            await repository.UpsertAsync(otherLanguage);
            await repository.UpsertAsync(pending);
            await repository.UpsertAsync(rejected);

            Assert.Equal(VotingServeResult.Recorded, await repository.RecordServedAsync("m1", "match-a:0"));
            Assert.Equal(VotingServeResult.AlreadyRecorded,
                await repository.RecordServedAsync("m1", "match-a:0"));
            var staleAuthoringEdit = VotingQuestion.Create("m1", Language.En, "m-friends", "Edited friend",
                VotingAnswerSource.Fixed, ["A", "B"], now, topic: "people/friends", status: QuestionStatus.Approved);
            await repository.UpsertAsync(staleAuthoringEdit);
            Assert.Equal(1, (await repository.GetAsync("m1"))!.TimesServed);
            Assert.Equal(VotingServeResult.AlreadyRecorded,
                await repository.RecordServedAsync("m1", "match-a:0"));
            Assert.Equal(VotingServeResult.Recorded, await repository.RecordServedAsync("m1", "match-b:0"));
            Assert.Equal(2, (await repository.GetAsync("m1"))!.TimesServed);

            var concurrent = await Task.WhenAll(
                repository.RecordServedAsync("m1", "parallel:duplicate"),
                repository.RecordServedAsync("m1", "parallel:duplicate"),
                repository.RecordServedAsync("m1", "parallel:a"),
                repository.RecordServedAsync("m1", "parallel:b"));
            Assert.Equal(3, concurrent.Count(result => result == VotingServeResult.Recorded));
            Assert.Equal(1, concurrent.Count(result => result == VotingServeResult.AlreadyRecorded));
            Assert.Equal(5, (await repository.GetAsync("m1"))!.TimesServed);

            var duplicate = VotingQuestion.Create("m9", Language.En, "m-friends", "Duplicate",
                VotingAnswerSource.Participants, null, now, topic: "people/friends");
            await Assert.ThrowsAsync<MongoWriteException>(() => repository.UpsertAsync(duplicate));
            var duplicateEmpty = VotingQuestion.Create("m8", Language.En, "m-friends", "Duplicate empty",
                VotingAnswerSource.Participants, null, now, topic: "");
            await Assert.ThrowsAsync<MongoWriteException>(() => repository.UpsertAsync(duplicateEmpty));

            var topics = await repository.ExistingTopicsAsync(Language.En);
            Assert.Contains("people/friends", topics);
            Assert.Contains("", topics);
            Assert.Equal(["people/friends"], await repository.ExistingTopicsAsync(Language.Fa));
            var sampled = await repository.SampleApprovedAsync(Language.En, "m-friends", 2, ["m1", "m4"]);
            Assert.Equal(2, sampled.Count);
            Assert.DoesNotContain(sampled, q => q.Id is "m1" or "m4");
            Assert.Equal(7, await repository.CountAsync(new VotingQuestionFilter()));
            Assert.Equal(6, await repository.CountAsync(new VotingQuestionFilter(Lang: Language.En)));
            Assert.Equal(6, await repository.CountAsync(new VotingQuestionFilter(CategoryId: "m-friends")));
            Assert.Equal(1, await repository.CountAsync(new VotingQuestionFilter(Status: QuestionStatus.Pending)));
            Assert.Equal(4, await repository.CountAsync(new VotingQuestionFilter(Text: "friend")));
            Assert.Equal(1, await repository.CountAsync(new VotingQuestionFilter(
                Language.En, "m-friends", QuestionStatus.Approved, "friend", 0, 10)));
            Assert.Equal(0, await repository.CountAsync(new VotingQuestionFilter(Text: ".")));
            var textMatches = await repository.FindAsync(new VotingQuestionFilter(Lang: Language.En, Text: "friend"));
            Assert.Equal(["m1", "m6", "m7"], textMatches.Select(q => q.Id).Order());
            Assert.Equal(["Another", "Edited friend", "Empty topic", "Other", "Rejected friend"],
                (await repository.ExistingPromptsAsync(Language.En, "m-friends")).Order());

            var ownedVoting = VotingQuestion.Create("owned-voting", Language.En, "m-friends", "Owner question",
                VotingAnswerSource.Participants, null, now, source: QuestionSource.Player,
                status: QuestionStatus.Approved, ownerId: "player-1");
            await repository.UpsertAsync(ownedVoting);
            Assert.DoesNotContain("owned-voting", (await repository.SampleApprovedAsync(Language.En, ContentScope.All, 100, []))
                .Select(q => q.Id));
            Assert.Equal(["owned-voting"], (await repository.SampleApprovedAsync(Language.En,
                new ContentScope(null, "player-1"), 10, [])).Select(q => q.Id));

            var uncategorized = VotingQuestion.Create("m-uncategorized", Language.En, null, "Uncategorized prompt",
                VotingAnswerSource.Participants, null, now, status: QuestionStatus.Approved);
            await repository.UpsertAsync(uncategorized);
            Assert.Empty(await repository.SampleApprovedAsync(Language.En, new ContentScope([]), 10, []));
            var allScope = await repository.SampleApprovedAsync(Language.En, ContentScope.All, 100, []);
            Assert.Contains(allScope, q => q.Id == uncategorized.Id);
            var categoryScope = await repository.SampleApprovedAsync(Language.En, new ContentScope(["m-friends"]), 100, []);
            Assert.All(categoryScope, q => Assert.Equal("m-friends", q.CategoryId));
            var collectionNames = await client.GetDatabase(dbName).ListCollectionNames().ToListAsync();
            Assert.Contains("categories", collectionNames);
            Assert.DoesNotContain("voting_categories", collectionNames);
            Assert.Contains("questions", collectionNames);
            Assert.DoesNotContain("voting_questions", collectionNames);
            Assert.Equal("Friends", (await categories.GetAsync("m-friends"))!.NameEn);

            var batchInsert = VotingQuestion.Create("m10", Language.En, "m-friends", "Batch row",
                VotingAnswerSource.Participants, null, now, topic: "batch-row");
            Assert.Equal(2, await repository.UpsertManyAsync([first, batchInsert]));
            Assert.NotNull(await repository.GetAsync("m10"));

            var triviaRepository = new MongoQuestionRepository(context);
            var trivia = Question.Create("same-topic-trivia", Language.En, "general", Difficulty.Easy,
                "Trivia", ["a", "b", "c", "d"], 0, now, topic: "people/trivia", status: QuestionStatus.Approved);
            var uncategorizedTrivia = Question.Create("uncategorized-trivia", Language.En, null, Difficulty.Easy,
                "Uncategorized trivia", ["a", "b", "c", "d"], 0, now, status: QuestionStatus.Approved);
            await triviaRepository.UpsertAsync(trivia);
            await triviaRepository.UpsertAsync(uncategorizedTrivia);
            var sameTopicTrivia = Question.Create("same-topic-trivia-two", Language.En, "general", Difficulty.Easy,
                "Second trivia", ["a", "b", "c", "d"], 0, now, topic: "people/friends");
            await triviaRepository.UpsertAsync(sameTopicTrivia);
            Assert.Null(await triviaRepository.GetAsync("m1"));
            Assert.Null(await repository.GetAsync(trivia.Id));
            var duplicateTriviaTopic = Question.Create("duplicate-trivia-topic", Language.En, "general", Difficulty.Easy,
                "Duplicate trivia", ["a", "b", "c", "d"], 0, now, topic: "people/friends");
            await Assert.ThrowsAsync<MongoWriteException>(() => triviaRepository.UpsertAsync(duplicateTriviaTopic));
            Assert.Null((await triviaRepository.GetAsync(uncategorizedTrivia.Id))!.CategoryId);
            Assert.Empty(await triviaRepository.SampleApprovedAsync(Language.En, new ContentScope([]), Difficulty.Easy, 10, []));
            var triviaAll = await triviaRepository.SampleApprovedAsync(Language.En, ContentScope.All, Difficulty.Easy, 10, []);
            Assert.Contains(triviaAll, q => q.Id == trivia.Id);
            Assert.Contains(triviaAll, q => q.Id == uncategorizedTrivia.Id);
            var triviaCategory = await triviaRepository.SampleApprovedAsync(Language.En, new ContentScope(["general"]), Difficulty.Easy, 10, []);
            Assert.All(triviaCategory, q => Assert.Equal("general", q.CategoryId));
            Assert.DoesNotContain("people/trivia", await repository.ExistingTopicsAsync(Language.En));
            var ownedTrivia = Question.Create("owned-trivia", Language.En, "general", Difficulty.Easy,
                "Owner trivia", ["a", "b", "c", "d"], 0, now, source: QuestionSource.Player,
                status: QuestionStatus.Approved, ownerId: "player-1");
            await triviaRepository.UpsertAsync(ownedTrivia);
            Assert.DoesNotContain("owned-trivia", (await triviaRepository.SampleApprovedAsync(Language.En,
                ContentScope.All, Difficulty.Easy, 100, [])).Select(q => q.Id));
            Assert.Equal(["owned-trivia"], (await triviaRepository.SampleApprovedAsync(Language.En,
                new ContentScope(["general"], "player-1"), Difficulty.Easy, 10, [])).Select(q => q.Id));

            var generationLog = new MongoVotingGenerationLog(context);
            var generationNow = new DateTimeOffset(
                now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
            var generation = new VotingGenerationRun("voting-run", generationNow, generationNow.AddSeconds(1), Language.En,
                "m-friends", VotingAnswerSource.Participants, 5, 4, 1, null);
            await generationLog.SaveAsync(generation);
            Assert.Equal(generation, Assert.Single(await generationLog.RecentAsync(10)));

            var triviaLog = new MongoGenerationLog(context);
            var triviaRun = new GenerationRun("trivia-run", generationNow, generationNow.AddSeconds(1), 3, 2, 1, null);
            await triviaLog.SaveAsync(triviaRun);
            Assert.Equal(triviaRun, Assert.Single(await triviaLog.RecentAsync(10)));
            Assert.Equal(QuestionFamily.Trivia, triviaRun.Family);
            Assert.Equal(QuestionFamily.Voting, generation.Family);
            var generationRows = await client.GetDatabase(dbName).GetCollection<BsonDocument>("generation_runs")
                .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
            Assert.Equal(2, generationRows.Count);
            Assert.Contains(generationRows, row => row["Family"].AsInt32 == (int)QuestionFamily.Trivia);
            Assert.Contains(generationRows, row => row["Family"].AsInt32 == (int)QuestionFamily.Voting);
        }
        finally
        {
            await client.DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public void Seed_categories_share_one_collection_and_family_allowlists_are_separate()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Seed", "categories.json");
        var rows = System.Text.Json.JsonSerializer.Deserialize<List<SeedCategory>>(
            File.ReadAllText(path), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        Assert.Contains(rows, x => x.Id == "friends" && x.NameEn == "Friends");
        Assert.Contains(rows, x => x.Id == "geography" && x.NameEn == "Geography");
        Assert.All(rows, x => Assert.False(x.Id.StartsWith("m-", StringComparison.Ordinal)));
        var settings = System.Text.Json.JsonSerializer.Deserialize<ContentSettings>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Seed", "content_settings.json")),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Contains("geography", settings.TriviaCategoryIds);
        Assert.Contains("friends", settings.VotingCategoryIds);
        Assert.DoesNotContain("friends", settings.TriviaCategoryIds);
    }

    [Fact]
    public void Seeder_has_one_unambiguous_production_constructor()
    {
        var constructors = typeof(Seeder).GetConstructors();
        Assert.Single(constructors);
        Assert.Equal([
            typeof(IQuestionRepository), typeof(ICategoryRepository), typeof(IContentSettingsRepository),
            typeof(IClock), typeof(ILogger<Seeder>)], constructors[0].GetParameters().Select(x => x.ParameterType));

        var services = new ServiceCollection();
        services.AddSingleton<IQuestionRepository, FakeQuestions>();
        services.AddSingleton<ICategoryRepository, FakeCategories>();
        services.AddSingleton<IClock>(new TimeProviderClock(TimeProvider.System));
        services.AddSingleton<IContentSettingsRepository, FakeContentSettingsRepository>();
        services.AddLogging();
        services.AddSingleton<Seeder>();
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<Seeder>());
    }

    [Fact]
    public async Task Seeder_uses_the_shared_category_repository_and_seeds_tenant_family_settings()
    {
        var root = Path.Combine(Path.GetTempPath(), "quesshi-seed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Seed"));
        try
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Seed", "categories.json"),
                Path.Combine(root, "Seed", "categories.json"));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Seed", "content_settings.json"),
                Path.Combine(root, "Seed", "content_settings.json"));
            var questions = new FakeQuestions();
            var categories = new FakeCategories();
            var content = new FakeContentSettingsRepository(categories);
            var seeder = new Seeder(questions, categories, content, new TimeProviderClock(TimeProvider.System),
                NullLogger<Seeder>.Instance);

            await seeder.RunAsync(root);
            Assert.Contains(categories.Items, c => c.Id == "friends");
            Assert.Contains(categories.Items, c => c.Id == "geography");
            Assert.Contains("friends", (await content.GetAsync()).VotingCategoryIds);
            var editedCategoryId = categories.Items[0].Id;
            await categories.UpsertAsync(categories.Items[0] with { NameEn = "Renamed" });
            await seeder.RunAsync(root);
            Assert.Equal("Renamed", (await categories.GetAsync(editedCategoryId))!.NameEn);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

}
