using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
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

        Assert.Equal(participants.Id, restoredParticipants.Id);
        Assert.Equal(participants.Lang, restoredParticipants.Lang);
        Assert.Equal(participants.MatchingCategoryId, restoredParticipants.MatchingCategoryId);
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
        Assert.Equal(fixedQuestion.MatchingCategoryId, restoredFixed.MatchingCategoryId);
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
            var secondNullTopic = MatchingQuestion.Create("m3", Language.En, "m-friends", "Another",
                MatchingAnswerSource.Participants, null, now, status: QuestionStatus.Approved);
            var emptyTopic = MatchingQuestion.Create("m4", Language.En, "m-friends", "Empty topic",
                MatchingAnswerSource.Participants, null, now, topic: "", status: QuestionStatus.Approved);
            var otherLanguage = MatchingQuestion.Create("m5", Language.Fa, "m-friends", "A friend",
                MatchingAnswerSource.Fixed, ["الف", "ب"], now, topic: "people/friends", status: QuestionStatus.Approved);
            var pending = MatchingQuestion.Create("m6", Language.En, "m-other", "Draft friend",
                MatchingAnswerSource.Fixed, ["A", "B"], now, topic: "draft", status: QuestionStatus.Pending);
            var rejected = MatchingQuestion.Create("m7", Language.En, "m-friends", "Rejected friend",
                MatchingAnswerSource.Fixed, ["A", "B"], now, topic: "rejected", status: QuestionStatus.Rejected);
            await repository.UpsertAsync(first);
            await repository.UpsertAsync(nullTopic);
            await repository.UpsertAsync(secondNullTopic);
            await repository.UpsertAsync(emptyTopic);
            await repository.UpsertAsync(otherLanguage);
            await repository.UpsertAsync(pending);
            await repository.UpsertAsync(rejected);

            Assert.Equal(MatchingServeResult.Recorded, await repository.RecordServedAsync("m1", "match-a:0"));
            Assert.Equal(MatchingServeResult.AlreadyRecorded,
                await repository.RecordServedAsync("m1", "match-a:0"));
            var staleAuthoringEdit = MatchingQuestion.Create("m1", Language.En, "m-friends", "Edited friend",
                MatchingAnswerSource.Fixed, ["A", "B"], now, topic: "people/friends", status: QuestionStatus.Approved);
            await repository.UpsertAsync(staleAuthoringEdit);
            Assert.Equal(1, (await repository.GetAsync("m1"))!.TimesServed);
            Assert.Equal(MatchingServeResult.AlreadyRecorded,
                await repository.RecordServedAsync("m1", "match-a:0"));
            Assert.Equal(MatchingServeResult.Recorded, await repository.RecordServedAsync("m1", "match-b:0"));
            Assert.Equal(2, (await repository.GetAsync("m1"))!.TimesServed);

            var concurrent = await Task.WhenAll(
                repository.RecordServedAsync("m1", "parallel:duplicate"),
                repository.RecordServedAsync("m1", "parallel:duplicate"),
                repository.RecordServedAsync("m1", "parallel:a"),
                repository.RecordServedAsync("m1", "parallel:b"));
            Assert.Equal(3, concurrent.Count(result => result == MatchingServeResult.Recorded));
            Assert.Equal(1, concurrent.Count(result => result == MatchingServeResult.AlreadyRecorded));
            Assert.Equal(5, (await repository.GetAsync("m1"))!.TimesServed);

            var duplicate = MatchingQuestion.Create("m9", Language.En, "m-friends", "Duplicate",
                MatchingAnswerSource.Participants, null, now, topic: "people/friends");
            await Assert.ThrowsAsync<MongoWriteException>(() => repository.UpsertAsync(duplicate));
            var duplicateEmpty = MatchingQuestion.Create("m8", Language.En, "m-friends", "Duplicate empty",
                MatchingAnswerSource.Participants, null, now, topic: "");
            await Assert.ThrowsAsync<MongoWriteException>(() => repository.UpsertAsync(duplicateEmpty));

            var topics = await repository.ExistingTopicsAsync(Language.En);
            Assert.Contains("people/friends", topics);
            Assert.Contains("", topics);
            Assert.Equal(["people/friends"], await repository.ExistingTopicsAsync(Language.Fa));
            var sampled = await repository.SampleApprovedAsync(Language.En, "m-friends", 2, ["m1", "m4"]);
            Assert.Equal(2, sampled.Count);
            Assert.DoesNotContain(sampled, q => q.Id is "m1" or "m4");
            Assert.Equal(7, await repository.CountAsync(new MatchingQuestionFilter()));
            Assert.Equal(6, await repository.CountAsync(new MatchingQuestionFilter(Lang: Language.En)));
            Assert.Equal(6, await repository.CountAsync(new MatchingQuestionFilter(CategoryId: "m-friends")));
            Assert.Equal(1, await repository.CountAsync(new MatchingQuestionFilter(Status: QuestionStatus.Pending)));
            Assert.Equal(4, await repository.CountAsync(new MatchingQuestionFilter(Text: "friend")));
            Assert.Equal(1, await repository.CountAsync(new MatchingQuestionFilter(
                Language.En, "m-friends", QuestionStatus.Approved, "friend", 0, 10)));
            Assert.Equal(0, await repository.CountAsync(new MatchingQuestionFilter(Text: ".")));
            var textMatches = await repository.FindAsync(new MatchingQuestionFilter(Lang: Language.En, Text: "friend"));
            Assert.Equal(["m1", "m6", "m7"], textMatches.Select(q => q.Id).Order());
            Assert.Equal(["Another", "Edited friend", "Empty topic", "Other", "Rejected friend"],
                (await repository.ExistingPromptsAsync(Language.En, "m-friends")).Order());

            var batchInsert = MatchingQuestion.Create("m10", Language.En, "m-friends", "Batch row",
                MatchingAnswerSource.Participants, null, now, topic: "batch-row");
            Assert.Equal(2, await repository.UpsertManyAsync([first, batchInsert]));
            Assert.NotNull(await repository.GetAsync("m10"));

            var trivia = Question.Create("same-topic-trivia", Language.En, "general", Difficulty.Easy,
                "Trivia", ["a", "b", "c", "d"], 0, now, topic: "people/trivia");
            await new MongoQuestionRepository(context).UpsertAsync(trivia);
            Assert.DoesNotContain("people/trivia", await repository.ExistingTopicsAsync(Language.En));

            var generationLog = new MongoMatchingGenerationLog(context);
            var generationNow = new DateTimeOffset(
                now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
            var generation = new MatchingGenerationRun("matching-run", generationNow, generationNow.AddSeconds(1), Language.En,
                "m-friends", MatchingAnswerSource.Participants, 5, 4, 1, null);
            await generationLog.SaveAsync(generation);
            Assert.Equal(generation, Assert.Single(await generationLog.RecentAsync(10)));
        }
        finally
        {
            await client.DropDatabaseAsync(dbName);
        }
    }

    [Fact]
    public void Matching_seed_file_has_the_exact_isolated_categories()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Seed", "matching_categories.json");
        var rows = System.Text.Json.JsonSerializer.Deserialize<List<SeedCategory>>(
            File.ReadAllText(path), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        Assert.Equal([
            "m-partners", "m-partners-living-together", "m-families-with-children",
            "m-friends", "m-colleagues", "m-teammates"], rows.Select(x => x.Id));
        Assert.Equal([
            "Partners", "Partners living together", "Families with children",
            "Friends", "Colleagues", "Teammates"], rows.Select(x => x.NameEn));
        Assert.Equal([
            "همسران", "همخانه‌ها", "خانواده‌های دارای فرزند", "دوستان", "همکاران", "هم‌تیمی‌ها"],
            rows.Select(x => x.NameFa));
        Assert.Equal([
            "Partners", "Samenwonende partners", "Gezinnen met kinderen", "Vrienden", "Collega's", "Teamgenoten"],
            rows.Select(x => x.NameNl));

        var trivia = System.Text.Json.JsonSerializer.Deserialize<List<SeedCategory>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Seed", "categories.json")),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.DoesNotContain(trivia, x => x.Id.StartsWith("m-", StringComparison.Ordinal));
        Assert.Empty(rows.Select(x => x.Id).Intersect(trivia.Select(x => x.Id)));
    }

    [Fact]
    public void Matching_seeder_has_one_unambiguous_production_constructor()
    {
        var constructors = typeof(Seeder).GetConstructors();
        Assert.Single(constructors);
        Assert.Equal([
            typeof(IQuestionRepository), typeof(ICategoryRepository), typeof(IMatchingCategoryRepository),
            typeof(IClock), typeof(ILogger<Seeder>)], constructors[0].GetParameters().Select(x => x.ParameterType));

        var services = new ServiceCollection();
        services.AddSingleton<IQuestionRepository, FakeQuestions>();
        services.AddSingleton<ICategoryRepository, FakeCategories>();
        services.AddSingleton<IMatchingCategoryRepository, FakeMatchingCategories>();
        services.AddSingleton<IClock>(new TimeProviderClock(TimeProvider.System));
        services.AddLogging();
        services.AddSingleton<Seeder>();
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<Seeder>());
    }

    [Fact]
    public void Matching_repository_ports_are_isolated_from_trivia_ports_and_models()
    {
        var triviaTypes = new HashSet<Type>
        {
            typeof(IQuestionRepository), typeof(ICategoryRepository), typeof(Question), typeof(Category),
            typeof(QuestionDoc), typeof(CategoryDoc)
        };
        var matchingPorts = new[] { typeof(IMatchingQuestionRepository), typeof(IMatchingCategoryRepository) };

        foreach (var port in matchingPorts)
        foreach (var method in port.GetMethods())
        {
            var values = method.GetParameters().Select(p => p.ParameterType).Append(method.ReturnType);
            foreach (var value in values)
            {
                var type = value;
                while (type.IsArray || type.IsByRef || type.IsPointer) type = type.GetElementType()!;
                if (type.IsGenericType)
                    foreach (var argument in type.GetGenericArguments())
                        Assert.DoesNotContain(argument, triviaTypes);
                Assert.DoesNotContain(type, triviaTypes);
            }
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
            var seeder = new Seeder(questions, categories, matching, new TimeProviderClock(TimeProvider.System),
                NullLogger<Seeder>.Instance);

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
