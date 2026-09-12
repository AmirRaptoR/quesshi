using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Quesshi.Domain;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

/// <summary>
/// <c>POST /api/admin/questions</c> for all three kinds, at the real HTTP layer.
///
/// <para>
/// Two things are being proved. The first is that the admin panel can author a sorting question and
/// a map question at all — the panel and the generator are the only two ways a question ever gets
/// written, and until this endpoint understood kinds the panel could write exactly one of the three.
/// </para>
/// <para>
/// The second is the validation table from <c>docs/sorting-and-map-questions.md</c>, one test per
/// row, asserting the <i>specific</i> error code rather than merely a 400. That distinction is the
/// whole point of the codes: a form that can only say "that didn't work" makes an admin guess which
/// of a dozen rules they broke, and the rules that say what a kind must <b>not</b> carry — a stray
/// map target on a choice question, four options left behind on a map question — are precisely the
/// ones nobody guesses.
/// </para>
/// </summary>
[Collection(nameof(LiveClusterCollection))]
public class AdminQuestionKindEndpointTests(LiveClusterFixture fixture) : IAsyncDisposable
{
    private readonly AdminApiTestHost _host = new(fixture.Cluster);

    private HttpClient AdminClient()
    {
        var client = _host.NewClient();
        var admin = AdminUser.Create($"aqk-admin-{Guid.NewGuid():N}", "admin", "admin@example.com", "hash", DateTimeOffset.UtcNow);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _host.AdminTokenIssuer.Issue(admin));
        return client;
    }

    private static SaveQuestionDto Choice(string prompt = "Which sea has no coastline?") =>
        new(null, "en", "geography", 2, prompt, ["a", "b", "c", "d"], 1, null, null, null, "approved");

    private static SaveQuestionDto Sort(string prompt = "Order these cities by population, largest first") =>
        new(null, "en", "geography", 2, prompt, ["Tokyo", "Delhi", "Cairo", "Lima"], 0, null, null, null, "approved",
            Kind: "sort");

    private static SaveQuestionDto MapCountry(string prompt = "Which country is shaped like this?") =>
        new(null, "en", "geography", 2, prompt, [], 0, null, null, null, "approved",
            Kind: "map", Target: new MapTargetDto("country", "NL"), BaseLayer: "borders");

    private static SaveQuestionDto MapCity(string prompt = "Where is the city of canals?") =>
        new(null, "en", "geography", 2, prompt, [], 0, null, null, null, "approved",
            Kind: "map", Target: new MapTargetDto("city", null, 52.37, 4.90, 150), BaseLayer: "blank");

    private static SaveQuestionDto Players(string prompt = "Who does the most work at home?") =>
        new(null, "en", "geography", 2, prompt, [], 0, null, null, null, "approved", Kind: "players");

    private static async Task<AdminQuestionDto> SavedAsync(HttpClient client, SaveQuestionDto body)
    {
        var response = await client.PostAsJsonAsync("/api/admin/questions", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<AdminQuestionDto>())!;
    }

    /// <summary>The error code out of a rejected save, or null if it was not rejected at all.</summary>
    private static async Task<string?> RejectionAsync(HttpClient client, SaveQuestionDto body)
    {
        var response = await client.PostAsJsonAsync("/api/admin/questions", body);
        if (response.IsSuccessStatusCode) return null;

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
    }

    // --- authoring ------------------------------------------------------------------

    /// <summary>
    /// The regression that matters most here: everything about writing and editing an ordinary
    /// multiple-choice question is exactly as it was, with no kind mentioned anywhere.
    /// </summary>
    [Fact]
    public async Task A_choice_question_is_still_written_and_edited_the_way_it_always_was()
    {
        using var client = AdminClient();

        var created = await SavedAsync(client, Choice($"Choice {Guid.NewGuid():N}?"));

        Assert.Equal("choice", created.Kind);
        Assert.Equal(1, created.CorrectIndex);
        Assert.Null(created.Target);
        Assert.Null(created.BaseLayer);

        var edited = await SavedAsync(client, Choice(created.Prompt) with
        {
            Id = created.Id, Choices = ["w", "x", "y", "z"], CorrectIndex = 3
        });

        Assert.Equal(created.Id, edited.Id);
        Assert.Equal(["w", "x", "y", "z"], edited.Choices);
        Assert.Equal(3, edited.CorrectIndex);
        Assert.Equal("choice", edited.Kind);
    }

    /// <summary>
    /// A sorting question is stored in its correct order. The author always sees the truth — the
    /// shuffle happens at serve time, seeded by the match — so what goes in is what comes back.
    /// </summary>
    [Fact]
    public async Task A_sorting_question_is_stored_in_its_correct_order()
    {
        using var client = AdminClient();

        var created = await SavedAsync(client, Sort($"Order these by population {Guid.NewGuid():N}"));

        Assert.Equal("sort", created.Kind);
        Assert.Equal(["Tokyo", "Delhi", "Cairo", "Lima"], created.Choices);
        Assert.Equal(0, created.CorrectIndex);
        Assert.Null(created.Target);

        var stored = LiveShared.Questions.Items.Single(q => q.Id == created.Id);
        Assert.Equal(QuestionKind.Sort, stored.Kind);
        Assert.Equal("Tokyo", stored.Choices[0]);
    }

    [Fact]
    public async Task A_country_map_question_is_stored_with_its_target_and_base_layer()
    {
        using var client = AdminClient();

        var created = await SavedAsync(client, MapCountry($"Which country {Guid.NewGuid():N}?"));

        Assert.Equal("map", created.Kind);
        Assert.Empty(created.Choices);
        Assert.Equal("country", created.Target!.Shape);
        Assert.Equal("NL", created.Target.CountryCode);
        Assert.Equal("borders", created.BaseLayer);

        var stored = LiveShared.Questions.Items.Single(q => q.Id == created.Id);
        Assert.True(stored.Target!.IsCountry);
        Assert.Equal(MapBaseLayer.Borders, stored.BaseLayer);
    }

    [Fact]
    public async Task A_city_map_question_keeps_its_coordinates_and_radius()
    {
        using var client = AdminClient();

        var created = await SavedAsync(client, MapCity($"Where is {Guid.NewGuid():N}?"));

        Assert.Equal("city", created.Target!.Shape);
        Assert.Equal(52.37, created.Target.Latitude);
        Assert.Equal(4.90, created.Target.Longitude);
        Assert.Equal(150, created.Target.RadiusKm);
        Assert.Equal("blank", created.BaseLayer);
    }

    /// <summary>
    /// Changing a question's kind moves its map fields with it, in both directions. This is the case
    /// the "must be null" half of the validation table was written for: an edit that changed the
    /// kind and left the old kind's fields behind is how a choice question ends up carrying a target
    /// nothing reads.
    /// </summary>
    [Fact]
    public async Task Editing_a_question_from_one_kind_to_another_replaces_its_map_fields()
    {
        using var client = AdminClient();

        var created = await SavedAsync(client, Choice($"Choice to map {Guid.NewGuid():N}?"));

        var asMap = await SavedAsync(client, MapCountry(created.Prompt) with { Id = created.Id });

        Assert.Equal("map", asMap.Kind);
        Assert.Empty(asMap.Choices);
        Assert.Equal("NL", asMap.Target!.CountryCode);

        var backToChoice = await SavedAsync(client, Choice(created.Prompt) with { Id = created.Id });

        Assert.Equal("choice", backToChoice.Kind);
        Assert.Null(backToChoice.Target);
        Assert.Null(backToChoice.BaseLayer);
        Assert.Equal(4, backToChoice.Choices.Count);
    }

    /// <summary>An admin authors a players question with nothing but a prompt — no choices, no
    /// correct index, no map fields, ever asked for or accepted.</summary>
    [Fact]
    public async Task A_players_question_is_written_with_just_a_prompt()
    {
        using var client = AdminClient();

        var created = await SavedAsync(client, Players($"Players {Guid.NewGuid():N}?"));

        Assert.Equal("players", created.Kind);
        Assert.Empty(created.Choices);
        Assert.Equal(0, created.CorrectIndex);
        Assert.Null(created.Target);
        Assert.Null(created.BaseLayer);

        var stored = LiveShared.Questions.Items.Single(q => q.Id == created.Id);
        Assert.Equal(QuestionKind.Players, stored.Kind);
    }

    [Fact]
    public async Task A_players_question_with_choices_left_on_it_is_refused()
    {
        using var client = AdminClient();

        Assert.Equal("players_has_choices", await RejectionAsync(client,
            Players($"Leftover choices {Guid.NewGuid():N}?") with { Choices = ["a", "b", "c", "d"] }));
    }

    // --- the validation table, one row at a time ------------------------------------

    [Fact]
    public async Task A_choice_question_carrying_a_map_target_is_refused()
    {
        using var client = AdminClient();

        Assert.Equal("stray_target", await RejectionAsync(client,
            Choice($"Stray target {Guid.NewGuid():N}?") with { Target = new MapTargetDto("country", "NL") }));
    }

    [Fact]
    public async Task A_sorting_question_carrying_a_base_layer_is_refused()
    {
        using var client = AdminClient();

        Assert.Equal("stray_base_layer", await RejectionAsync(client,
            Sort($"Stray layer {Guid.NewGuid():N}") with { BaseLayer = "borders" }));
    }

    [Fact]
    public async Task A_map_question_with_choices_left_on_it_is_refused()
    {
        using var client = AdminClient();

        Assert.Equal("map_has_choices", await RejectionAsync(client,
            MapCountry($"Leftover choices {Guid.NewGuid():N}?") with { Choices = ["a", "b", "c", "d"] }));
    }

    [Fact]
    public async Task A_map_question_with_no_target_is_refused()
    {
        using var client = AdminClient();

        Assert.Equal("target_required", await RejectionAsync(client,
            MapCountry($"No target {Guid.NewGuid():N}?") with { Target = null }));
    }

    [Fact]
    public async Task A_map_question_with_no_base_layer_is_refused()
    {
        using var client = AdminClient();

        Assert.Equal("base_layer_required", await RejectionAsync(client,
            MapCountry($"No layer {Guid.NewGuid():N}?") with { BaseLayer = null }));
    }

    /// <summary>A real ISO code the bundled map has no path for. The question would be unanswerable
    /// and undrawable, so it is not a question.</summary>
    [Theory]
    [InlineData("ZZ", "unknown_country")]
    [InlineData("Nederland", "bad_country_code")]
    [InlineData("", "bad_country_code")]
    public async Task A_country_target_the_map_cannot_draw_is_refused(string code, string expected)
    {
        using var client = AdminClient();

        Assert.Equal(expected, await RejectionAsync(client,
            MapCountry($"Unknown country {Guid.NewGuid():N}?") with { Target = new MapTargetDto("country", code) }));
    }

    [Theory]
    [InlineData(2d)]
    [InlineData(9000d)]
    [InlineData(null)]
    public async Task A_city_radius_outside_what_the_game_can_draw_is_refused(double? radiusKm)
    {
        using var client = AdminClient();

        Assert.Equal("bad_radius", await RejectionAsync(client,
            MapCity($"Bad radius {Guid.NewGuid():N}?") with { Target = new MapTargetDto("city", null, 52.37, 4.90, radiusKm) }));
    }

    [Fact]
    public async Task A_city_with_a_latitude_off_the_globe_is_refused()
    {
        using var client = AdminClient();

        Assert.Equal("bad_latitude", await RejectionAsync(client,
            MapCity($"Off the globe {Guid.NewGuid():N}?") with { Target = new MapTargetDto("city", null, 120, 4.90, 150) }));
    }

    /// <summary>
    /// NaN, over the wire, for real. It cannot travel as a JSON number — the format has no literal
    /// for it — but <c>JsonSerializerDefaults.Web</c> reads a quoted <c>"NaN"</c> into a double
    /// happily, so this is a shape a client can genuinely send and therefore one the endpoint has to
    /// refuse. It matters more than it looks: NaN passes a range check written as
    /// <c>lat is &lt; -90 or &gt; 90</c>, and then every distance computed from it is NaN, which
    /// compares false against any radius — a question nobody can ever answer, with nothing anywhere
    /// reporting a fault.
    /// </summary>
    [Fact]
    public async Task A_city_with_a_NaN_latitude_is_refused()
    {
        using var client = AdminClient();

        var body = $$"""
        {
          "lang": "en", "categoryId": "geography", "level": 2,
          "prompt": "NaN latitude {{Guid.NewGuid():N}}?",
          "choices": [], "correctIndex": 0, "status": "approved",
          "kind": "map", "baseLayer": "borders",
          "target": { "shape": "city", "latitude": "NaN", "longitude": 4.90, "radiusKm": 150 }
        }
        """;

        var response = await client.PostAsync("/api/admin/questions",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("bad_latitude", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_target_that_is_neither_a_country_nor_a_city_is_refused()
    {
        using var client = AdminClient();

        Assert.Equal("bad_target_shape", await RejectionAsync(client,
            MapCountry($"Bad shape {Guid.NewGuid():N}?") with { Target = new MapTargetDto("continent", "NL") }));
    }

    [Fact]
    public async Task A_kind_nobody_defines_is_refused_rather_than_read_as_choice()
    {
        using var client = AdminClient();

        Assert.Equal("bad_kind", await RejectionAsync(client, Choice($"Bad kind {Guid.NewGuid():N}?") with { Kind = "jigsaw" }));
    }

    /// <summary>The choice rules still bite, and still say which one bit.</summary>
    [Theory]
    [InlineData(3, "bad_choices")]
    [InlineData(4, "bad_correct_index")]
    public async Task The_ordinary_choice_rules_still_report_themselves_separately(int choices, string expected)
    {
        using var client = AdminClient();

        var body = Choice($"Ordinary rules {Guid.NewGuid():N}?") with
        {
            Choices = [.. Enumerable.Range(0, choices).Select(i => $"option {i}")],
            CorrectIndex = choices == 4 ? 9 : 0
        };

        Assert.Equal(expected, await RejectionAsync(client, body));
    }

    /// <summary>
    /// A sorting question's answer is the order it is stored in, so there is no index to point at.
    /// The domain pins it to zero; an admin form that sent something else is telling the server one
    /// thing while the stored order says another.
    /// </summary>
    [Fact]
    public async Task A_sorting_question_with_a_correct_index_is_refused()
    {
        using var client = AdminClient();

        Assert.Equal("bad_correct_index", await RejectionAsync(client,
            Sort($"Indexed sort {Guid.NewGuid():N}") with { CorrectIndex = 2 }));
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();
}
