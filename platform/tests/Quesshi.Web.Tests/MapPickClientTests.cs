using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Kinds.MapPick;
using Quesshi.Kinds.MapPick.Client;
using Quesshi.Shared;
using Quesshi.Web.Plugins;
using MapPickRevealComponent = Quesshi.Kinds.MapPick.Client.MapPickReveal;

namespace Quesshi.Web.Tests;

public sealed class MapPickClientTests : BunitContext
{
    private static readonly JsonSerializerOptions FixtureJsonOptions = new() { PropertyNameCaseInsensitive = true };

    public MapPickClientTests()
    {
        Services.AddSingleton(new HttpClient(new WorldMapHandler()) { BaseAddress = new Uri("https://tenant.example/") });
    }

    [Fact]
    public async Task Entry_registers_explicit_v1_map_component_roots()
    {
        var registry = new ClientRegistry(new NoopLoader());
        new MapPickClientEntry().Register(registry);

        var client = await registry.ResolveAsync("map_pick");

        Assert.Same(typeof(MapPickPlay), client!.Play(1));
        Assert.Same(typeof(MapPickRevealComponent), client.Reveal(1));
        Assert.Same(typeof(MapPickEditor), client.Editor);
    }

    [Theory]
    [InlineData("basic", "DE")]
    [InlineData("point", "0,179.9")]
    public async Task Play_emits_a_normalizable_answer_for_each_fixture(string fixtureName, string expected)
    {
        var fixture = Fixture(fixtureName);
        var payload = Read<MapPickPayload>(fixture, "payload");
        var card = Read<MapPickCard>(fixture, "card");
        object? emitted = null;
        var cut = Render<MapPickPlay>(parameters => parameters
            .Add(component => component.Card, card)
            .Add(component => component.OnAnswer, EventCallback.Factory.Create<object?>(this, value => emitted = value)));
        var field = cut.FindComponent<MapField>();
        var selection = card.Shape == MapTargetKind.Country ? MapSelection.Country("DE") : MapSelection.Point(0, 179.9);

        await cut.InvokeAsync(() => field.Instance.SelectionChanged.InvokeAsync(selection));
        await cut.InvokeAsync(() => field.Instance.OnConfirm.InvokeAsync());

        var answer = Assert.IsType<MapPickAnswer>(emitted);
        Assert.Equal(expected, answer.CountryCode ?? $"{answer.Latitude:0.####},{answer.Longitude:0.####}");
        var kind = new MapPickKind();
        Assert.IsType<MapPickStored>(kind.Normalize(new Item<MapPickPayload, MapPickResolved>(payload, new MapPickResolved(payload.Target!)), answer));
    }

    [Theory]
    [InlineData("basic")]
    [InlineData("point")]
    public async Task Disabled_play_does_not_emit_another_answer(string fixtureName)
    {
        var card = Read<MapPickCard>(Fixture(fixtureName), "card");
        var emissions = 0;
        var cut = Render<MapPickPlay>(parameters => parameters
            .Add(component => component.Card, card)
            .Add(component => component.Disabled, true)
            .Add(component => component.OnAnswer, EventCallback.Factory.Create<object?>(this, _ => emissions++)));
        var field = cut.FindComponent<MapField>();

        await cut.InvokeAsync(() => field.Instance.SelectionChanged.InvokeAsync(MapSelection.Country("NL")));
        await cut.InvokeAsync(() => field.Instance.OnConfirm.InvokeAsync());

        Assert.Equal(0, emissions);
    }

    [Theory]
    [InlineData("basic", "Germany", "Netherlands")]
    [InlineData("point", "0°, 179.9°", "0°, -179.9°")]
    public void Reveal_renders_the_target_and_two_stored_fixture_answers(string fixtureName, string target, string playerAnswer)
    {
        var fixture = Fixture(fixtureName);
        var cut = Render<MapPickRevealComponent>(parameters => parameters
            .Add(component => component.Card, Read<MapPickCard>(fixture, "card"))
            .Add(component => component.Reveal, Read<Quesshi.Kinds.MapPick.MapPickReveal>(fixture, "reveal"))
            .Add(component => component.Answers, StoredAnswers(fixture)));

        Assert.Contains(target, cut.Markup);
        Assert.Contains("Player 1", cut.Markup);
        Assert.Contains(playerAnswer, cut.Markup);
        Assert.Contains("Player 2", cut.Markup);
    }

    [Fact]
    public void World_map_keeps_successful_country_marks_and_editor_point_tolerance_visible()
    {
        var world = Render<WorldMap>(parameters => parameters
            .Add(component => component.Marks, new[] { new MapMark(MapSelection.Country("DE"), "Player", true) }));

        Assert.Contains("worldmap__country is-picked", world.Markup);

        var pointPayload = Read<MapPickPayload>(Fixture("point"), "payload");
        var editor = Render<MapPickEditor>(parameters => parameters
            .Add(component => component.Kind, "map_pick")
            .Add(component => component.Payload, pointPayload)
            .Add(component => component.Lang, "en")
            .Add(component => component.PayloadChanged, EventCallback.Factory.Create<object?>(this, _ => { })));

        Assert.Contains("worldmap__tolerance", editor.Markup);
    }

    [Fact]
    public void Map_text_and_asset_parser_keep_country_and_point_labels_stable()
    {
        var countries = WorldMapAsset.Parse("<svg><path id=\"DE\" d=\"M0,0\"><title>Germany</title></path></svg>");

        Assert.Equal("Germany", MapText.CountryName(countries, "de"));
        Assert.Equal("DE", MapText.CountryName([new MapCountry("DE", string.Empty, string.Empty)], "DE"));
        Assert.Equal("ZZ", MapText.CountryName(countries, "ZZ"));
        Assert.Equal(string.Empty, MapText.CountryName(countries, null));
        Assert.Equal("52.82°, 4.07°", MapText.Coordinates(MapSelection.Point(52.817797, 4.067797)));
    }

    [Theory]
    [InlineData("basic")]
    [InlineData("point")]
    public async Task Editor_round_trips_a_fixture_payload_unchanged(string fixtureName)
    {
        var payload = Read<MapPickPayload>(Fixture(fixtureName), "payload");
        var writes = new List<object?>();

        var cut = Render<MapPickEditor>(parameters => parameters
            .Add(component => component.Kind, "map_pick")
            .Add(component => component.Payload, payload)
            .Add(component => component.Lang, "en")
            .Add(component => component.PayloadChanged, EventCallback.Factory.Create<object?>(this, writes.Add)));

        Assert.Empty(writes);
        Assert.DoesNotContain("form-error", cut.Markup, StringComparison.Ordinal);

        var mapField = cut.FindComponent<MapField>();
        var selection = payload.Target!.Shape == MapTargetKind.Country
            ? MapSelection.Country(payload.Target.CountryCode!)
            : MapSelection.Point(payload.Target.Latitude!.Value, payload.Target.Longitude!.Value);
        await cut.InvokeAsync(() => mapField.Instance.SelectionChanged.InvokeAsync(selection));

        Assert.Equal(payload, Assert.IsType<MapPickPayload>(Assert.Single(writes)));
    }

    [Theory]
    [InlineData("basic", "ZZ", "drawable ISO")]
    [InlineData("point", "91", "between -90 and 90")]
    public async Task Editor_uses_kind_validation_and_shows_inline_problems(string fixtureName, string value, string problem)
    {
        var payload = Read<MapPickPayload>(Fixture(fixtureName), "payload");
        var invalidTarget = payload.Target!.Shape == MapTargetKind.Country
            ? payload.Target with { CountryCode = value }
            : payload.Target with { Latitude = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture) };
        var invalidPayload = payload with { Target = invalidTarget };
        var cut = Render<MapPickEditor>(parameters => parameters
            .Add(component => component.Kind, "map_pick")
            .Add(component => component.Payload, invalidPayload)
            .Add(component => component.Lang, "en")
            .Add(component => component.PayloadChanged, EventCallback.Factory.Create<object?>(this, _ => { })));

        var expectedProblems = new MapPickKind().Validate(invalidPayload, new ValidateEnv("en"));
        Assert.NotEmpty(expectedProblems);
        Assert.Contains(problem, cut.Markup);
    }

    [Fact]
    public async Task Map_hosts_select_the_map_client_for_country_and_point_cards()
    {
        var registry = new ClientRegistry(new NoopLoader());
        new MapPickClientEntry().Register(registry);
        Services.AddSingleton<IClientRegistry>(registry);

        foreach (var fixtureName in new[] { "basic", "point" })
        {
            var cut = Render<KindPlay>(parameters => parameters
                .Add(component => component.Card, new KindCard("map_pick", 1, Read<MapPickCard>(Fixture(fixtureName), "card")))
                .Add(component => component.OnAnswer, EventCallback.Factory.Create<object?>(this, _ => { })));
            await cut.InvokeAsync(() => Task.CompletedTask);
            Assert.Single(cut.FindComponents<MapPickPlay>());

            var reveal = Render<KindReveal>(parameters => parameters
                .Add(component => component.Card, new KindCard("map_pick", 1, Read<MapPickCard>(Fixture(fixtureName), "card")))
                .Add(component => component.Reveal, Read<Quesshi.Kinds.MapPick.MapPickReveal>(Fixture(fixtureName), "reveal"))
                .Add(component => component.Answers, StoredAnswers(Fixture(fixtureName))));
            Assert.Single(reveal.FindComponents<MapPickRevealComponent>());

            var editor = Render<KindEditor>(parameters => parameters
                .Add(component => component.Kind, "map_pick")
                .Add(component => component.Payload, Read<MapPickPayload>(Fixture(fixtureName), "payload"))
                .Add(component => component.PayloadChanged, EventCallback.Factory.Create<object?>(this, _ => { }))
                .Add(component => component.Lang, "en"));
            Assert.Single(editor.FindComponents<MapPickEditor>());
        }
    }

    private static JsonElement Fixture(string name)
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            var fixture = Path.Combine(current.FullName, "platform", "tests", "Quesshi.Kinds.Tests", "Fixtures", "map_pick", $"{name}.json");
            if (!File.Exists(fixture)) fixture = Path.Combine(current.FullName, "tests", "Quesshi.Kinds.Tests", "Fixtures", "map_pick", $"{name}.json");
            if (File.Exists(fixture)) return JsonDocument.Parse(File.ReadAllText(fixture)).RootElement.Clone();
            current = current.Parent;
        }
        throw new FileNotFoundException($"Could not find the {name} map_pick fixture.");
    }

    private static T Read<T>(JsonElement fixture, string property) =>
        JsonSerializer.Deserialize<T>(fixture.GetProperty(property), FixtureJsonOptions) ?? throw new InvalidDataException($"Fixture {property} is invalid.");

    private static IReadOnlyList<MapPickStored> StoredAnswers(JsonElement fixture) =>
        fixture.GetProperty("answers").EnumerateArray()
            .Where(answer => answer.TryGetProperty("stored", out _))
            .Select(answer => JsonSerializer.Deserialize<MapPickStored>(answer.GetProperty("stored"), FixtureJsonOptions)!)
            .Take(2)
            .ToArray();

    private sealed class WorldMapHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<svg><path id=\"DE\" d=\"M0,0\"><title>Germany</title></path><path id=\"NL\" d=\"M1,1\"><title>Netherlands</title></path></svg>", Encoding.UTF8, "image/svg+xml"),
            });
    }

    private sealed class NoopLoader : IClientAssemblyLoader
    {
        public Task<IReadOnlyList<System.Reflection.Assembly>> LoadAsync(string assemblyName) => Task.FromResult<IReadOnlyList<System.Reflection.Assembly>>([]);
    }
}
