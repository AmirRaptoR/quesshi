using System.Globalization;
using System.Text.Json;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Server.Seed;

/// <summary>
/// Loads the starter categories and question bank from JSON. Idempotent: question ids are derived
/// from their content, so re-running updates rather than duplicates. No translated text lives in
/// this file — it all comes from the JSON beside it.
/// </summary>
public sealed class Seeder(IQuestionRepository questions, ICategoryRepository categories, IClock clock, ILogger<Seeder> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task RunAsync(string contentRoot, CancellationToken ct = default)
    {
        var folder = Path.Combine(contentRoot, "Seed");

        foreach (var row in await ReadAsync<SeedCategory>(Path.Combine(folder, "categories.json"), ct))
            if (await categories.GetAsync(row.Id, ct) is null)
                await categories.UpsertAsync(new Category(row.Id, row.NameFa, row.NameEn, row.Icon, row.Color, true, row.SortOrder, row.NameNl), ct);

        var inserted = 0;

        // Every questions.<lang>*.json in the folder, so the bank can be kept in per-topic files
        // instead of one unreviewable megafile. Order is fixed so a reseed is reproducible.
        var files = new[] { (Language.Fa, "fa"), (Language.En, "en"), (Language.Nl, "nl") }
            .SelectMany(x => Directory.EnumerateFiles(folder, $"questions.{x.Item2}*.json").Order().Select(f => (File: f, Lang: x.Item1)));

        foreach (var (file, lang) in files)
        {
            var batch = new List<Question>();

            foreach (var row in await ReadAsync<SeedRow>(file, ct))
            {
                var id = StableId(lang, row);

                // The seed files always list the answer first; shuffle so players cannot just tap option one.
                var (choices, correctIndex) = Shuffle(row.C, row.A, id);

                if (await questions.GetAsync(id, ct) is { } existing)
                {
                    // The id is derived from the prompt alone, so a corrected set of choices carries the
                    // same id and would otherwise never reach a bank that has already been seeded. Only
                    // seeded questions are refreshed, so an admin's edits are not overwritten, and Edit
                    // keeps the play counts and reports the question has collected.
                    if (existing.Source != QuestionSource.Seed || !Differs(existing, row, choices, correctIndex)) continue;

                    existing.Edit(lang, row.Cat, (Difficulty)row.Level, row.Q, choices, correctIndex, ToMedia(row.Media), row.E);
                    batch.Add(existing);
                    continue;
                }

                batch.Add(Question.Create(id, lang, row.Cat, (Difficulty)row.Level, row.Q, choices, correctIndex,
                    clock.Now, ToMedia(row.Media), row.E, QuestionSource.Seed, QuestionStatus.Approved));
            }

            if (batch.Count > 0)
            {
                await questions.UpsertManyAsync(batch, ct);
                inserted += batch.Count;
            }
        }

        logger.LogInformation("Seeding done: {Inserted} new questions", inserted);
    }

    private async Task<List<T>> ReadAsync<T>(string path, CancellationToken ct)
    {
        if (File.Exists(path)) return JsonSerializer.Deserialize<List<T>>(await File.ReadAllTextAsync(path, ct), Json) ?? [];

        logger.LogWarning("Seed file {Path} is missing", path);
        return [];
    }

    private static MediaRef? ToMedia(SeedMedia? media)
        => media is null || !Enum.TryParse<MediaKind>(media.Kind, true, out var kind)
            ? null
            : new MediaRef(kind, media.Url, media.Attribution);

    /// <summary>
    /// Deterministic per question, so a reseed reproduces the same layout instead of reshuffling.
    /// The seed is read out of the id's own hex rather than from GetHashCode, which .NET randomises
    /// per process — that made the old "deterministic" shuffle a different shuffle on every start.
    /// </summary>
    public static (List<string> Choices, int CorrectIndex) Shuffle(List<string> choices, int correct, string seed)
    {
        var rng = new Random(unchecked((int)uint.Parse(seed[..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
        var indexed = choices.Select((text, i) => (text, isCorrect: i == correct))
            .OrderBy(_ => rng.Next())
            .ToList();

        return ([.. indexed.Select(x => x.text)], indexed.FindIndex(x => x.isCorrect));
    }

    /// <summary>Whether anything a player would notice has changed since this row was last seeded.</summary>
    private static bool Differs(Question existing, SeedRow row, List<string> choices, int correctIndex)
        => existing.Level != (Difficulty)row.Level
        || existing.CorrectIndex != correctIndex
        || existing.Explanation != row.E
        || !existing.Choices.SequenceEqual(choices);

    private static string StableId(Language lang, SeedRow row)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{lang}|{row.Cat}|{row.Q}"));
        return Convert.ToHexString(bytes)[..24].ToLowerInvariant();
    }
}
