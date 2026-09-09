using System.Text.Json;
using Quesshi.Domain;
using Quesshi.Infrastructure.Generation;

namespace Quesshi.Server.Tests;

/// <summary>
/// What the model is actually asked for, for the two new kinds.
///
/// <para>
/// A prompt is not usually worth a test — wording is tuned, and a test that pinned wording would be
/// changed every time it was tuned. These pin two things that are not wording but requirements, each
/// of which is invisible everywhere else in the codebase:
/// </para>
/// <para>
/// The sorting prompt must demand an objective, measurable criterion, because <b>nothing downstream
/// can</b>. No validation rule can decide whether "order these by importance" is objective, so the
/// prompt is the only place that rule exists at all, and deleting the paragraph would remove the
/// rule with no test failing anywhere unless one is written here.
/// </para>
/// <para>
/// The map prompt must ask for a city's country as well as its coordinates, because the pipeline's
/// one real defence against a mislocated city is checking those two against each other. Stop asking
/// for the country and the check silently starts rejecting everything (no code to check against) or
/// waving everything through, depending on how it is written.
/// </para>
/// </summary>
public class GenerationPromptTests
{
    private static readonly QuestionPromptBuilder Prompts = new();
    private static readonly Category Geography = new("geography", "جغرافیا", "Geography", "*", "#fff");

    private static string Sort(Language lang = Language.En) => Prompts.Sort(lang, Geography, Difficulty.Medium, 5, []);
    private static string Map(Language lang = Language.En) => Prompts.Map(lang, Geography, Difficulty.Medium, 5, []);

    [Theory]
    [InlineData("MEASURABLE")]
    [InlineData("population")]
    [InlineData("NEVER order by taste")]
    public void The_sorting_prompt_demands_a_measurable_criterion(string expected)
        => Assert.Contains(expected, Sort(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The criterion is no use to a player who cannot see it, so the prompt they read has to
    /// carry it — that is a rule about the generated text, not about the schema.</summary>
    [Fact]
    public void The_sorting_prompt_requires_the_criterion_to_reach_the_player()
        => Assert.Contains("State the criterion and its direction in the prompt the player reads", Sort());

    /// <summary>The items come back in the correct order, because the shuffle happens at serve time.</summary>
    [Fact]
    public void The_sorting_prompt_asks_for_the_items_in_the_correct_order()
        => Assert.Contains("IN THE CORRECT ORDER", Sort());

    [Theory]
    [InlineData(Language.Fa, "Persian")]
    [InlineData(Language.Nl, "Dutch")]
    [InlineData(Language.En, "English")]
    public void Both_new_prompts_name_the_language_they_are_written_in(Language lang, string expected)
    {
        Assert.Contains(expected, Sort(lang));
        Assert.Contains(expected, Map(lang));
    }

    [Fact]
    public void The_map_prompt_asks_for_a_city_and_the_country_it_is_in()
    {
        Assert.Contains("THE COUNTRY THAT CITY IS IN", Map());
        Assert.Contains("latitude and longitude", Map());
    }

    /// <summary>Telling the model the check exists is part of the prompt's job: a model that knows
    /// its coordinates will be verified reaches for the ones it is sure of.</summary>
    [Fact]
    public void The_map_prompt_says_that_the_coordinates_are_checked()
        => Assert.Contains("the question is thrown away", Map());

    /// <summary>A prompt naming the place has no answer left to find.</summary>
    [Fact]
    public void The_map_prompt_forbids_naming_the_answer()
        => Assert.Contains("must NOT contain the answer", Map());

    /// <summary>The radius bounds in the prompt are the ones the domain enforces, not a second pair
    /// of numbers typed into a string — a prompt that asked for 5 km would be asking for questions
    /// that get thrown away.</summary>
    [Fact]
    public void The_map_prompt_quotes_the_radius_bounds_the_domain_enforces()
    {
        Assert.Contains($"between {MapTarget.MinRadiusKm} and {MapTarget.MaxRadiusKm}", Map());
    }

    /// <summary>
    /// The schemas are anonymous objects handed straight to a JSON serialiser, so the only thing
    /// that proves they are the shape OpenRouter's structured output requires is serialising them.
    /// </summary>
    [Fact]
    public void The_sorting_schema_asks_for_ordered_items_and_no_correct_index()
    {
        var schema = JsonSerializer.Serialize(SortSchema.ResponseFormat);

        Assert.Contains("quesshi_sort_questions", schema);
        Assert.Contains("CORRECT order", schema);
        Assert.DoesNotContain("correctIndex", schema);
    }

    [Fact]
    public void The_map_schema_requires_a_country_code_for_every_target()
    {
        var schema = JsonSerializer.Serialize(MapSchema.ResponseFormat);
        using var document = JsonDocument.Parse(schema);

        var required = document.RootElement
            .GetProperty("json_schema").GetProperty("schema")
            .GetProperty("properties").GetProperty("questions")
            .GetProperty("items").GetProperty("required")
            .EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Contains("countryCode", required);
        Assert.Contains("targetKind", required);

        // Present-and-null rather than absent: strict structured output has no way to say "omit
        // this", so a country question sends explicit nulls for the city's three fields.
        Assert.Contains("latitude", required);
        Assert.Contains("\"null\"", schema);
    }
}
