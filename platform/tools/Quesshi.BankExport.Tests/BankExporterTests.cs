using System.Collections.Concurrent;
using System.Text.Json;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Quesshi.BankExport;
using Xunit.Sdk;

namespace Quesshi.BankExport.Tests;

public sealed class BankExporterTests
{
    [SkippableFact]
    public async Task Export_preserves_every_supported_question_shape_and_legacy_metadata()
    {
        var connection = Connection();
        Skip.If(connection is null, "MongoDB is not configured.");
        await using var fixture = await Fixture.CreateAsync(connection!);
        var bundle = fixture.BundleDirectory;
        await fixture.WriteAsync(
        [
            new() { Id = "choice", Family = 0, Lang = 0, CategoryId = "history", Level = 1, Prompt = "Choice", Choices = ["a", "b", "c", "d"], CorrectIndex = 2, Status = 1, Source = 1, Topic = "topic-choice" },
            new() { Id = "order", Family = 0, Kind = 1, Lang = 1, CategoryId = "history", Level = 2, Prompt = "Order", Choices = ["one", "two", "three", "four"], Status = 2, Source = 2 },
            new() { Id = "map", Family = 0, Kind = 2, Lang = 2, CategoryId = "places", Level = 3, Prompt = "Map", TargetShape = 1, TargetLatitude = 52.3676, TargetLongitude = 4.9041, TargetRadiusKm = 2.5, BaseLayer = 1 },
            new() { Id = "country", Family = 0, Kind = 2, Lang = 2, CategoryId = "places", Level = 3, Prompt = "Country", TargetShape = 0, TargetCountryCode = "NL", BaseLayer = 0 },
            new() { Id = "poll-choice", Family = 1, Lang = 0, CategoryId = "history", Prompt = "Poll", AnswerSource = 1, FixedChoices = ["yes", "no"] },
            new() { Id = "poll-roster", Family = 1, Lang = 1, CategoryId = "places", Prompt = "Roster", AnswerSource = 0 },
        ], cancellationToken: CancellationToken.None);

        var exitCode = await Program.Main([
            "--connection", connection!, "--database", fixture.DatabaseName, "--output", fixture.BundleDirectory, "--media-root", fixture.MediaDirectory,
        ]);
        var files = Directory.EnumerateFiles(bundle, "*.json").Select(Path.GetFileName).Select(name => name!).OrderBy(name => name).ToArray();

        Assert.Equal(0, exitCode);
        Assert.Equal(["categories.json", "choice.json", "map_pick.json", "order.json", "poll_choice.json", "poll_roster.json", "report.json", "settings.json"], files);
        Assert.True(Directory.Exists(Path.Combine(bundle, "media")));
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(bundle, "report.json"), CancellationToken.None));
        Assert.Equal(6, report.RootElement.GetProperty("exportedKinds").EnumerateObject().Sum(entry => entry.Value.GetInt32()));
        Assert.Equal(2, report.RootElement.GetProperty("exportedLanguages").GetProperty("fa").GetInt32());
        Assert.Equal(1, report.RootElement.GetProperty("exportedStatuses").GetProperty("approved").GetInt32());
        using var categories = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(bundle, "categories.json"), CancellationToken.None));
        Assert.Equal("history", categories.RootElement[0].GetProperty("slug").GetString());
        Assert.Equal("تاریخ", categories.RootElement[0].GetProperty("names").GetProperty("fa").GetString());
        Assert.Equal("History", categories.RootElement[0].GetProperty("names").GetProperty("en").GetString());
        Assert.Equal("Geschiedenis", categories.RootElement[0].GetProperty("names").GetProperty("nl").GetString());
        Assert.Equal("book", categories.RootElement[0].GetProperty("icon").GetString());
        Assert.Equal("blue", categories.RootElement[0].GetProperty("color").GetString());
        Assert.Equal(1, categories.RootElement[0].GetProperty("sortOrder").GetInt32());
        Assert.True(categories.RootElement[0].GetProperty("isActive").GetBoolean());
        using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(bundle, "settings.json"), CancellationToken.None));
        Assert.Equal("history", settings.RootElement.GetProperty("triviaCategorySlugs")[0].GetString());
        Assert.Equal("places", settings.RootElement.GetProperty("votingCategorySlugs")[0].GetString());
        using var choice = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(bundle, "choice.json"), CancellationToken.None));
        Assert.Equal("ai", choice.RootElement[0].GetProperty("source").GetString());
        Assert.Equal(2, choice.RootElement[0].GetProperty("correctIndex").GetInt32());
        using var map = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(bundle, "map_pick.json"), CancellationToken.None));
        Assert.Equal(52.3676, map.RootElement[0].GetProperty("latitude").GetDouble());
        Assert.Equal("NL", map.RootElement[1].GetProperty("countryCode").GetString());
        using var poll = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(bundle, "poll_choice.json"), CancellationToken.None));
        Assert.False(poll.RootElement[0].TryGetProperty("level", out _));
    }

    [SkippableFact]
    public async Task Export_skips_trivia_without_a_valid_level_but_exports_voting_without_one()
    {
        var connection = Connection();
        Skip.If(connection is null, "MongoDB is not configured.");
        await using var fixture = await Fixture.CreateAsync(connection!);
        await fixture.WriteAsync(
        [
            new() { Id = "missing-level", Family = 0, Lang = 0, CategoryId = "history", Prompt = "Missing", Choices = ["a", "b", "c", "d"], CorrectIndex = 0 },
            new() { Id = "invalid-level", Family = 0, Lang = 0, CategoryId = "history", Level = 6, Prompt = "Invalid", Choices = ["a", "b", "c", "d"], CorrectIndex = 0 },
            new() { Id = "vote", Family = 1, Lang = 0, CategoryId = "history", Prompt = "Vote", AnswerSource = 0 },
        ], CancellationToken.None);

        var report = await new BankExporter(fixture.Client, fixture.Options).ExportAsync(CancellationToken.None);

        Assert.Equal(["invalid-level", "missing-level"], report.Skipped.Where(question => question.Reason == "question_invalid").Select(question => question.QuestionId).OrderBy(id => id));
        using var poll = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.BundleDirectory, "poll_roster.json"), CancellationToken.None));
        Assert.False(poll.RootElement[0].TryGetProperty("level", out _));
    }

    [SkippableFact]
    public async Task Export_omits_blank_category_values_and_reports_unresolved_allow_list_categories()
    {
        var connection = Connection();
        Skip.If(connection is null, "MongoDB is not configured.");
        await using var fixture = await Fixture.CreateAsync(connection!);
        await fixture.WriteAsync(
        [new() { Id = "question", Family = 0, Lang = 0, CategoryId = "history", Level = 1, Prompt = "Question", Choices = ["a", "b", "c", "d"], CorrectIndex = 0 }],
        CancellationToken.None,
        categories:
        [new() { Id = "history", NameFa = "تاریخ", NameEn = "History", NameNl = "", Icon = "", Color = "", SortOrder = 4, IsActive = false }],
        settings: new() { TriviaCategoryIds = ["history", "missing-trivia"], VotingCategoryIds = ["history", "missing-voting"] });

        var report = await new BankExporter(fixture.Client, fixture.Options).ExportAsync(CancellationToken.None);
        using var categories = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.BundleDirectory, "categories.json"), CancellationToken.None));
        using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.BundleDirectory, "settings.json"), CancellationToken.None));

        var category = categories.RootElement[0];
        Assert.Equal("history", category.GetProperty("slug").GetString());
        Assert.Equal("تاریخ", category.GetProperty("names").GetProperty("fa").GetString());
        Assert.Equal("History", category.GetProperty("names").GetProperty("en").GetString());
        Assert.False(category.GetProperty("names").TryGetProperty("nl", out _));
        Assert.False(category.TryGetProperty("icon", out _));
        Assert.False(category.TryGetProperty("color", out _));
        Assert.Equal(4, category.GetProperty("sortOrder").GetInt32());
        Assert.False(category.GetProperty("isActive").GetBoolean());
        Assert.Equal(["history"], settings.RootElement.GetProperty("triviaCategorySlugs").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(["history"], settings.RootElement.GetProperty("votingCategorySlugs").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(["missing-trivia", "missing-voting"], report.MissingCategoryReferences.Where(reference => reference.Scope == "settings").Select(reference => reference.QuestionId).OrderBy(id => id));
    }

    [SkippableFact]
    public async Task Export_copies_safe_media_and_reports_missing_outside_and_player_owned_questions()
    {
        var connection = Connection();
        Skip.If(connection is null, "MongoDB is not configured.");
        await using var fixture = await Fixture.CreateAsync(connection!);
        await File.WriteAllBytesAsync(Path.Combine(fixture.MediaDirectory, "photo.png"), [1, 2, 3], CancellationToken.None);
        await File.WriteAllBytesAsync(Path.Combine(Directory.GetParent(fixture.MediaDirectory)!.FullName, "secret.png"), [4, 5, 6], CancellationToken.None);
        await fixture.WriteAsync(
        [
            new() { Id = "local", Family = 0, Lang = 0, CategoryId = "history", Level = 1, Prompt = "Local", Choices = ["a", "b", "c", "d"], CorrectIndex = 0, MediaUrl = "/media/photo.png", MediaKind = 1, MediaAttribution = "Credit" },
            new() { Id = "missing", Family = 0, Lang = 0, CategoryId = "history", Level = 1, Prompt = "Missing", Choices = ["a", "b", "c", "d"], CorrectIndex = 0, MediaUrl = "missing.png" },
            new() { Id = "outside", Family = 0, Lang = 0, CategoryId = "history", Level = 1, Prompt = "Outside", Choices = ["a", "b", "c", "d"], CorrectIndex = 0, MediaUrl = "../secret.png" },
            new() { Id = "outside-windows", Family = 0, Lang = 0, CategoryId = "history", Level = 1, Prompt = "Outside Windows", Choices = ["a", "b", "c", "d"], CorrectIndex = 0, MediaUrl = "..\\secret.png" },
            new() { Id = "outside-absolute", Family = 0, Lang = 0, CategoryId = "history", Level = 1, Prompt = "Outside Absolute", Choices = ["a", "b", "c", "d"], CorrectIndex = 0, MediaUrl = Path.Combine(Path.GetTempPath(), "secret.png") },
            new() { Id = "remote", Family = 0, Lang = 0, CategoryId = "history", Level = 1, Prompt = "Remote", Choices = ["a", "b", "c", "d"], CorrectIndex = 0, MediaUrl = "https://example.test/media.png", MediaKind = 1 },
            new() { Id = "player", Family = 0, Lang = 0, CategoryId = "history", Level = 1, Prompt = "Player", Choices = ["a", "b", "c", "d"], CorrectIndex = 0, Source = 3 },
            new() { Id = "invalid", Family = 0, Kind = 99, Lang = 0, CategoryId = "history", Level = 1, Prompt = "Invalid" },
            new() { Id = "unknown-category", Family = 0, Lang = 0, CategoryId = "unknown", Level = 1, Prompt = "Unknown", Choices = ["a", "b", "c", "d"], CorrectIndex = 0 },
        ], cancellationToken: CancellationToken.None);

        var report = await new BankExporter(fixture.Client, fixture.Options).ExportAsync(CancellationToken.None);
        using var exported = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.BundleDirectory, "choice.json"), CancellationToken.None));

        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(Path.Combine(fixture.BundleDirectory, "media", "photo.png"), CancellationToken.None));
        Assert.Equal("media/photo.png", exported.RootElement[0].GetProperty("mediaUrl").GetString());
        Assert.Equal("Credit", exported.RootElement[0].GetProperty("mediaAttribution").GetString());
        Assert.Equal("https://example.test/media.png", exported.RootElement[5].GetProperty("mediaUrl").GetString());
        Assert.Equal("missing", report.MissingMedia.Single().QuestionId);
        Assert.Equal("missing.png", report.MissingMedia.Single().Value);
        Assert.Equal(["../secret.png", "..\\secret.png", Path.Combine(Path.GetTempPath(), "secret.png")], report.OutsideMedia.Select(reference => reference.Value).OrderBy(value => value));
        Assert.False(File.Exists(Path.Combine(fixture.BundleDirectory, "media", "secret.png")));
        Assert.Equal(["category_missing", "player_owned", "unsupported_trivia_shape"], report.Skipped.Select(question => question.Reason).OrderBy(reason => reason));
        Assert.Equal("unknown-category", report.MissingCategoryReferences.Single(reference => reference.Scope == "question").QuestionId);
    }

    [SkippableFact]
    public async Task Export_splits_question_files_at_the_platform_import_row_limit()
    {
        var connection = Connection();
        Skip.If(connection is null, "MongoDB is not configured.");
        await using var fixture = await Fixture.CreateAsync(connection!);
        var questions = Enumerable.Range(0, BankExporter.MaximumRowsPerFile + 1)
            .Select(index => new LegacyQuestion
            {
                Id = "choice-" + index,
                Family = 0,
                Lang = 0,
                CategoryId = "history",
                Level = 1,
                Prompt = "Choice " + index,
                Choices = ["a", "b", "c", "d"],
                CorrectIndex = 0,
            }).ToArray();
        await fixture.WriteAsync(questions, CancellationToken.None);

        await new BankExporter(fixture.Client, fixture.Options).ExportAsync(CancellationToken.None);
        using var first = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.BundleDirectory, "choice-001.json"), CancellationToken.None));
        using var second = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.BundleDirectory, "choice-002.json"), CancellationToken.None));

        Assert.Equal(BankExporter.MaximumRowsPerFile, first.RootElement.GetArrayLength());
        Assert.Equal(1, second.RootElement.GetArrayLength());
    }

    [SkippableFact]
    public async Task Export_splits_at_the_import_byte_limit_and_skips_a_row_that_cannot_fit()
    {
        var connection = Connection();
        Skip.If(connection is null, "MongoDB is not configured.");
        await using var fixture = await Fixture.CreateAsync(connection!);
        await fixture.WriteAsync([new() { Id = "too-large", Family = 0, Lang = 0, CategoryId = "history", Level = 1, Prompt = new string('x', 2_000), Choices = ["a", "b", "c", "d"], CorrectIndex = 0 }], CancellationToken.None);

        var report = await new BankExporter(fixture.Client, fixture.Options, maximumBytesPerFile: 1_024).ExportAsync(CancellationToken.None);

        Assert.Contains(report.Skipped, question => question.QuestionId == "too-large" && question.Reason == "row_exceeds_import_byte_limit");
    }

    [SkippableFact]
    public async Task Export_splits_large_rows_without_exceeding_the_platform_import_byte_limit()
    {
        var connection = Connection();
        Skip.If(connection is null, "MongoDB is not configured.");
        await using var fixture = await Fixture.CreateAsync(connection!);
        var largePrompt = new string('x', 1_050_000);
        var questions = Enumerable.Range(0, 21).Select(index => new LegacyQuestion
        {
            Id = "large-" + index,
            Family = 0,
            Lang = 0,
            CategoryId = "history",
            Level = 1,
            Prompt = largePrompt,
            Choices = ["a", "b", "c", "d"],
            CorrectIndex = 0,
        }).ToArray();
        await fixture.WriteAsync(questions, CancellationToken.None);

        await new BankExporter(fixture.Client, fixture.Options).ExportAsync(CancellationToken.None);
        var parts = Directory.EnumerateFiles(fixture.BundleDirectory, "choice-*.json").ToArray();

        Assert.True(parts.Length > 1);
        Assert.All(parts, path => Assert.True(new FileInfo(path).Length <= BankExporter.MaximumBytesPerFile));
        Assert.Equal(questions.Length, parts.Sum(path => JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetArrayLength()));
    }

    [SkippableFact]
    public async Task Export_uses_only_read_commands_against_mongodb()
    {
        var connection = Connection();
        Skip.If(connection is null, "MongoDB is not configured.");
        await using var fixture = await Fixture.CreateAsync(connection!);
        await fixture.WriteAsync([new() { Id = "readonly", Family = 0, Lang = 0, CategoryId = "history", Level = 1, Prompt = "Read only", Choices = ["a", "b", "c", "d"], CorrectIndex = 0 }], cancellationToken: CancellationToken.None);
        var commands = new ConcurrentQueue<(string Name, string? Collection)>();
        var settings = MongoClientSettings.FromConnectionString(connection);
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandStartedEvent>(eventData => commands.Enqueue((eventData.CommandName, eventData.CommandName == "find" ? eventData.Command["find"].AsString : null)));
        var monitored = new MongoClient(settings);

        await new BankExporter(monitored, fixture.Options).ExportAsync(CancellationToken.None);

        var observed = commands.ToArray();
        Assert.NotEmpty(observed);
        Assert.Equal(["categories", "content_settings", "questions"], observed.Where(command => command.Name == "find").Select(command => command.Collection).OrderBy(collection => collection));
        Assert.All(observed, command => Assert.Contains(command.Name, new[] { "find", "getMore", "hello", "isMaster", "ping", "endSessions", "killCursors", "authenticate", "saslStart", "saslContinue" }));
    }

    private static string? Connection() => Environment.GetEnvironmentVariable("QUESSHI_TEST_MONGO");

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root;
        private Fixture(IMongoClient client, string databaseName, string root)
        {
            Client = client;
            DatabaseName = databaseName;
            this.root = root;
            BundleDirectory = Path.Combine(root, "bundle");
            MediaDirectory = Path.Combine(root, "media");
            Options = new("unused", databaseName, BundleDirectory, MediaDirectory);
        }
        public IMongoClient Client { get; }
        public string DatabaseName { get; }
        public string BundleDirectory { get; }
        public string MediaDirectory { get; }
        public ExportOptions Options { get; }
        public static Task<Fixture> CreateAsync(string connection)
        {
            var root = Path.Combine(Path.GetTempPath(), "quesshi-bank-export", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "media"));
            return Task.FromResult(new Fixture(new MongoClient(connection), "bank_export_" + Guid.NewGuid().ToString("N"), root));
        }
        public async Task WriteAsync(IReadOnlyList<LegacyQuestion> questions, CancellationToken cancellationToken, IReadOnlyList<LegacyCategory>? categories = null, LegacyContentSettings? settings = null)
        {
            var database = Client.GetDatabase(DatabaseName);
            await database.GetCollection<LegacyCategory>("categories").InsertManyAsync(
            categories ??
            [new() { Id = "history", NameFa = "تاریخ", NameEn = "History", NameNl = "Geschiedenis", Icon = "book", Color = "blue", SortOrder = 1, IsActive = true },
             new() { Id = "places", NameFa = "مکان", NameEn = "Places", NameNl = "Plaatsen", Icon = "map", Color = "green", SortOrder = 2, IsActive = false }], cancellationToken: cancellationToken);
            await database.GetCollection<LegacyContentSettings>("content_settings").InsertOneAsync(settings ?? new() { TriviaCategoryIds = ["history"], VotingCategoryIds = ["places"] }, cancellationToken: cancellationToken);
            await database.GetCollection<LegacyQuestion>("questions").InsertManyAsync(questions, cancellationToken: cancellationToken);
        }
        public async ValueTask DisposeAsync()
        {
            await Client.DropDatabaseAsync(DatabaseName);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
