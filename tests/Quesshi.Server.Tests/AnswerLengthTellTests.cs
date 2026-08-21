using System.Text.Json;
using Quesshi.Server.Seed;

namespace Quesshi.Server.Tests;

/// <summary>
/// A player who always picks the longest option should do no better than one who guesses. Writing
/// the right answer as a careful definition and the wrong ones as three quick dismissals is the
/// natural way to write a question and it hands the game away — the tell is readable without
/// knowing any Dutch, or any of the subject.
/// </summary>
public class AnswerLengthTellTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Seed");

    private const double Chance = 0.25;

    /// <summary>Room for the noise in a small file, but far below a usable strategy.</summary>
    private const double Ceiling = 0.40;

    /// <summary>
    /// Banks written before this rule existed. They are between 41% and 55%, which is a real tell
    /// worth clearing; they are listed rather than excluded silently so the debt stays visible and
    /// the list can only shrink. Do not add to it.
    /// </summary>
    private static readonly HashSet<string> WrittenBeforeTheRule =
    [
        "questions.en.art.json", "questions.en.food.json", "questions.en.nature.json",
        "questions.fa.art.json", "questions.fa.food.json", "questions.fa.literature.json",
        "questions.fa.movies.json", "questions.fa.music.json", "questions.fa.nature.json",
        "questions.fa.science.json", "questions.fa.technology.json"
    ];

    public static TheoryData<string> SeedFiles()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.EnumerateFiles(Folder, "questions.*.json").Order())
            data.Add(Path.GetFileName(path));
        return data;
    }

    [Theory]
    [MemberData(nameof(SeedFiles))]
    public void Picking_the_longest_option_is_no_better_than_guessing(string file)
    {
        var rows = JsonSerializer.Deserialize<List<SeedRow>>(File.ReadAllText(Path.Combine(Folder, file)), Json)!;

        var rate = Strategy(rows, longest: true);
        var floor = Strategy(rows, longest: false);

        if (WrittenBeforeTheRule.Contains(file))
        {
            // Still guarded, just at the level they were left at: they may not get worse.
            Assert.True(rate <= 0.60, $"{file}: the length tell got worse — {rate:P0} of questions go to the longest option");
            return;
        }

        Assert.True(rate <= Ceiling,
            $"{file}: picking the longest option wins {rate:P0}, against {Chance:P0} for guessing. " +
            "Write the wrong options at the same length and in the same shape as the right one.");

        // And the inverse, or a fixed bank simply teaches players to avoid the longest instead.
        Assert.True(floor <= Ceiling,
            $"{file}: picking the shortest option wins {floor:P0}, against {Chance:P0} for guessing.");
    }

    /// <summary>How often "always pick the longest (or shortest)" lands on the answer, splitting ties.</summary>
    private static double Strategy(List<SeedRow> rows, bool longest)
    {
        var wins = 0.0;

        foreach (var row in rows)
        {
            var lengths = row.C.Select(c => c.Length).ToList();
            var target = longest ? lengths.Max() : lengths.Min();
            var tied = lengths.Count(l => l == target);

            if (lengths[row.A] == target) wins += 1.0 / tied;
        }

        return wins / rows.Count;
    }
}

/// <summary>
/// The shuffle has to survive a restart. It was seeded from string.GetHashCode, which .NET
/// randomises per process, so every start dealt the options differently — invisible while a reseed
/// skipped questions it had already stored, and a full rewrite of the bank once it stopped.
/// </summary>
public class SeedShuffleTests
{
    private const string Id = "1f2f739b29af7450bdd7a34b";
    private static readonly List<string> Choices = ["alpha", "bravo", "charlie", "delta"];

    [Fact]
    public void The_same_question_is_dealt_the_same_way_every_time()
    {
        // Pinned values: if the shuffle stops being stable across processes, this run disagrees
        // with the one that wrote them down.
        var (choices, correct) = Seeder.Shuffle(Choices, 0, Id);

        Assert.Equal(["delta", "bravo", "alpha", "charlie"], choices);
        Assert.Equal(2, correct);
        Assert.Equal("alpha", choices[correct]);
    }

    [Fact]
    public void A_different_question_is_dealt_differently()
        => Assert.NotEqual(
            Seeder.Shuffle(Choices, 0, Id).Choices,
            Seeder.Shuffle(Choices, 0, "abcdef0123456789abcdef01").Choices);

    [Fact]
    public void The_answer_travels_with_its_text()
    {
        var (choices, correct) = Seeder.Shuffle(Choices, 2, Id);
        Assert.Equal("charlie", choices[correct]);
    }
}
