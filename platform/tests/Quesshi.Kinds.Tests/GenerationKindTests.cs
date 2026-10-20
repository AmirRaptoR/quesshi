using System.Text.Json;
using Quesshi.Kinds.Choice;
using Quesshi.Kinds.MapPick;
using Quesshi.Kinds.Order;
using Quesshi.Kinds.PollChoice;
using Quesshi.Kinds.PollRoster;
using Quesshi.Shared;

namespace Quesshi.Kinds.Tests;

public sealed class GenerationKindTests
{
    [Fact]
    public void Registry_returns_a_stable_non_generic_generation_surface()
    {
        var registry = new KindRegistry([new ChoiceKind()]);

        Assert.True(registry.TryGetCurrentGenerator("choice", out _));
    }

    [Fact]
    public void Choice_exports_a_candidate_schema()
    {
        var properties = Generator(new ChoiceKind()).CandidateSchema.GetProperty("properties");

        Assert.True(properties.TryGetProperty("choices", out _));
    }

    [Fact]
    public void Choice_candidate_passes_the_post_check()
    {
        var problems = Generator(new ChoiceKind()).PostCheck(Json("""{"choices":["Mercury","Venus","Earth","Mars"],"correctIndex":2}"""));

        Assert.Empty(problems);
    }

    [Fact]
    public void Choice_candidate_converts_to_a_choice_payload()
    {
        var payload = Generator(new ChoiceKind()).ToPayload(Json("""{"choices":["Mercury","Venus","Earth","Mars"],"correctIndex":2}"""));

        Assert.Equal("""{"options":["Mercury","Venus","Earth","Mars"],"correctIndex":2}""", payload.GetRawText());
    }

    [Fact]
    public void Order_candidate_passes_the_post_check()
    {
        var problems = Generator(new OrderKind()).PostCheck(Json("""{"criterion":"population in 2025","items":["Tokyo","Delhi","Shanghai","Sao Paulo"]}"""));

        Assert.Empty(problems);
    }

    [Fact]
    public void Order_candidate_converts_to_an_order_payload()
    {
        var payload = Generator(new OrderKind()).ToPayload(Json("""{"criterion":"population in 2025","items":["Tokyo","Delhi","Shanghai","Sao Paulo"]}"""));

        Assert.Equal("""{"items":[{"id":"item1","text":"Tokyo"},{"id":"item2","text":"Delhi"},{"id":"item3","text":"Shanghai"},{"id":"item4","text":"Sao Paulo"}]}""", payload.GetRawText());
    }

    [Fact]
    public void Order_prompt_requires_a_measurable_criterion()
    {
        var prompt = Generator(new OrderKind()).PromptFragment;

        Assert.Contains("measurable", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void City_inside_its_named_country_passes_the_post_check()
    {
        var problems = Generator(new MapPickKind()).PostCheck(Json("""{"targetKind":"city","countryCode":"NL","latitude":52.3676,"longitude":4.9041,"radiusKm":25}"""));

        Assert.Empty(problems);
    }

    [Fact]
    public void City_inside_its_named_country_converts_without_a_country()
    {
        var payload = JsonSerializer.Deserialize(
            Generator(new MapPickKind()).ToPayload(Json("""{"targetKind":"city","countryCode":"NL","latitude":52.3676,"longitude":4.9041,"radiusKm":25}""")),
            MapPickJsonContext.Default.MapPickPayload);

        Assert.Equal(new MapPickPayload(MapBaseLayer.Borders, new(MapTargetKind.Point, null, 52.3676, 4.9041, 25)), payload);
    }

    [Fact]
    public void City_outside_its_named_country_is_rejected()
    {
        var problems = Generator(new MapPickKind()).PostCheck(Json("""{"targetKind":"city","countryCode":"NL","latitude":48.8566,"longitude":2.3522,"radiusKm":25}"""));

        Assert.Contains(problems, problem => problem.Code == "target.coordinates.outsideCountry");
    }

    [Fact]
    public void City_with_a_country_absent_from_the_map_is_rejected()
    {
        var problems = Generator(new MapPickKind()).PostCheck(Json("""{"targetKind":"city","countryCode":"ZZ","latitude":52.3676,"longitude":4.9041,"radiusKm":25}"""));

        Assert.Contains(problems, problem => problem.Code == "target.country.invalid");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    public void Poll_choice_candidate_with_two_to_eight_options_converts_to_a_payload(int count)
    {
        var choices = Enumerable.Range(1, count).Select(number => $"Option {number}").ToArray();
        var candidate = JsonSerializer.SerializeToElement(new { choices });
        var payload = JsonSerializer.Deserialize(Generator(new PollChoiceKind()).ToPayload(candidate), PollChoiceJsonContext.Default.PollChoicePayload);

        Assert.Equal(count, payload!.Options!.Count);
    }

    [Fact]
    public void Poll_roster_candidate_converts_to_an_empty_payload()
    {
        var payload = JsonSerializer.Deserialize(Generator(new PollRosterKind()).ToPayload(Json("{}")), PollRosterJsonContext.Default.PollRosterPayload);

        Assert.Null(payload!.Extra);
    }

    private static IGenerates Generator(IKindRuntime kind) => Assert.IsAssignableFrom<IGenerates>(kind);

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }
}
