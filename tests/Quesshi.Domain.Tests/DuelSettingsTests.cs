namespace Quesshi.Domain.Tests;

/// <summary>
/// <see cref="DuelSettings"/> needs value equality on <see cref="DuelSettings.CategoryIds"/> and
/// <see cref="DuelSettings.Levels"/> specifically because the record-synthesized <c>Equals</c> would
/// otherwise compare them by reference (their declared type, <c>IReadOnlyList&lt;T&gt;</c>, has no
/// <c>IEquatable</c> of its own) — two independently built lists with identical contents, exactly what
/// crosses an HTTP request twice, would never be equal. That equality is load-bearing for
/// <c>UpdateSettingsAsync</c>'s equal-settings no-op rule (issue #104): a capacity-only change must be
/// recognised as "settings unchanged" even though the request rebuilt every list from scratch.
/// </summary>
public class DuelSettingsTests
{
    [Fact]
    public void Trivia_is_the_zero_value_and_legacy_json_without_mode_reads_as_trivia()
    {
        Assert.Equal(0, (int)GameMode.Trivia);
        var settings = System.Text.Json.JsonSerializer.Deserialize<DuelSettings>(
            "{\"Language\":0,\"QuestionCount\":10,\"CategoryIds\":[],\"Levels\":[]}");

        Assert.NotNull(settings);
        Assert.Equal(GameMode.Trivia, settings!.Mode);
    }

    [Fact]
    public void Mode_participates_in_value_equality()
    {
        var trivia = DuelSettings.Create(Language.En, 10, [], []);
        var matching = DuelSettings.Create(Language.En, 10, [], [], GameMode.Matching);

        Assert.NotEqual(trivia, matching);
        Assert.NotEqual(trivia.GetHashCode(), matching.GetHashCode());
    }

    [Fact]
    public void Matching_rejects_difficulty_levels()
        => Assert.Throws<ArgumentException>(() =>
            DuelSettings.Create(Language.En, 10, [], [Difficulty.Easy], GameMode.Matching));

    [Fact]
    public void Two_instances_with_the_same_content_but_different_list_references_are_equal()
    {
        var a = DuelSettings.Create(Language.En, 10, new List<string> { "geography" }, new List<Difficulty> { Difficulty.Easy });
        var b = DuelSettings.Create(Language.En, 10, new List<string> { "geography" }, new List<Difficulty> { Difficulty.Easy });

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Two_instances_with_no_restrictions_are_equal_even_though_each_empty_list_is_a_distinct_instance()
    {
        var a = DuelSettings.Create(Language.En, 10, [], []);
        var b = DuelSettings.Create(Language.En, 10, new List<string>(), new List<Difficulty>());

        Assert.Equal(a, b);
    }

    [Fact]
    public void Differing_category_ids_are_not_equal()
    {
        var a = DuelSettings.Create(Language.En, 10, ["geography"], []);
        var b = DuelSettings.Create(Language.En, 10, ["history"], []);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Differing_levels_are_not_equal()
    {
        var a = DuelSettings.Create(Language.En, 10, [], [Difficulty.Easy]);
        var b = DuelSettings.Create(Language.En, 10, [], [Difficulty.Hard]);

        Assert.NotEqual(a, b);
    }
}
