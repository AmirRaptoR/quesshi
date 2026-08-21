using System.Text.Json;
using Quesshi.Domain;
using Quesshi.Server.Seed;

namespace Quesshi.Server.Tests;

/// <summary>
/// The question bank is hand-written JSON, so nothing but a test stops a bad row reaching players.
/// Runs against the real seed files, which are copied next to the test assembly by the Server project.
/// </summary>
public class SeedBankTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Seed");

    private static List<T> Read<T>(string file)
        => JsonSerializer.Deserialize<List<T>>(File.ReadAllText(Path.Combine(Folder, file)), Json)!;

    private static IEnumerable<(string File, Language Lang, SeedRow Row)> AllRows()
    {
        foreach (var (lang, code) in new[] { (Language.Fa, "fa"), (Language.En, "en"), (Language.Nl, "nl") })
            foreach (var path in Directory.EnumerateFiles(Folder, $"questions.{code}*.json").Order())
                foreach (var row in Read<SeedRow>(Path.GetFileName(path)))
                    yield return (Path.GetFileName(path), lang, row);
    }

    [Fact]
    public void Every_row_is_a_valid_question()
    {
        foreach (var (file, _, row) in AllRows())
        {
            var failure = $"{file}: {row.Q}";

            Assert.False(string.IsNullOrWhiteSpace(row.Q), failure);
            Assert.InRange(row.Level, 1, 5);

            // Same gate the domain applies: four distinct non-blank choices, one of them correct.
            var ex = Record.Exception(() => Question.Validate(row.Q, row.C, row.A));
            Assert.True(ex is null, $"{failure} — {ex?.Message}");
        }
    }

    [Fact]
    public void Every_row_points_at_a_category_that_exists()
    {
        var known = Read<SeedCategory>("categories.json").Select(c => c.Id).ToHashSet();

        foreach (var (file, _, row) in AllRows())
            Assert.True(known.Contains(row.Cat), $"{file}: unknown category '{row.Cat}' on \"{row.Q}\"");
    }

    /// <summary>
    /// Ids are derived from language, category and prompt, so two rows that agree on all three
    /// silently collide and one of them never reaches the bank.
    /// </summary>
    [Fact]
    public void No_prompt_is_repeated_within_a_language()
    {
        var duplicates = AllRows()
            .GroupBy(x => (x.Lang, x.Row.Q))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.Lang}: {g.Key.Q}")
            .ToList();

        Assert.Empty(duplicates);
    }

    /// <summary>
    /// A match ramps across all five levels, so an empty bucket sends the builder into its fallback
    /// path and the ramp quietly flattens. Not every category exists in every language — KNM is
    /// Dutch and the general bank is not — so the rule is per language a category is stocked in at
    /// all: start it, and you finish all five levels.
    /// </summary>
    [Fact]
    public void A_category_stocked_in_a_language_is_stocked_at_every_level()
    {
        var stocked = AllRows().Select(x => (x.Lang, x.Row.Cat, x.Row.Level)).ToHashSet();
        var started = stocked.Select(x => (x.Lang, x.Cat)).ToHashSet();

        var gaps = from pair in started
                   from level in Enumerable.Range(1, 5)
                   where !stocked.Contains((pair.Lang, pair.Cat, level))
                   select $"{pair.Lang}/{pair.Cat}/level {level}";

        Assert.Empty(gaps);
    }

    /// <summary>Every category a player can be shown has to be playable in at least one language.</summary>
    [Fact]
    public void Every_category_is_stocked_in_at_least_one_language()
    {
        var started = AllRows().Select(x => (x.Lang, x.Row.Cat)).ToHashSet();

        // A bank that ships no seed questions at all is a choice, not a gap.
        if (started.Count == 0) return;

        var orphans = Read<SeedCategory>("categories.json")
            .Where(c => !started.Any(s => s.Cat == c.Id))
            .Select(c => c.Id);

        Assert.Empty(orphans);
    }
}
