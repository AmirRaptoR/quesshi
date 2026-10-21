using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
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
        await LoadWorldMapAsync();
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
        await LoadWorldMapAsync();
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

    [Fact]
    public async Task Play_discards_a_selection_when_the_host_moves_to_a_new_card()
    {
        await LoadWorldMapAsync();
        var countryCard = Read<MapPickCard>(Fixture("basic"), "card");
        var pointCard = Read<MapPickCard>(Fixture("point"), "card");
        var emissions = new List<object?>();
        var onAnswer = EventCallback.Factory.Create<object?>(this, emissions.Add);
        var cut = Render<PlayHost>(parameters => parameters
            .Add(component => component.InitialCard, countryCard)
            .Add(component => component.NextCard, pointCard)
            .Add(component => component.OnAnswer, onAnswer));

        await cut.InvokeAsync(() => cut.FindComponent<MapField>().Instance.SelectionChanged.InvokeAsync(MapSelection.Country("DE")));
        await cut.InvokeAsync(cut.Instance.MoveToNextCard);
        await cut.InvokeAsync(() => cut.FindComponent<MapField>().Instance.OnConfirm.InvokeAsync());

        Assert.Empty(emissions);
    }

    [Theory]
    [InlineData("basic", "Germany", "Netherlands")]
    [InlineData("point", "0°, 179.9°", "0°, -179.9°")]
    public async Task Reveal_renders_the_target_and_two_stored_fixture_answers(string fixtureName, string target, string playerAnswer)
    {
        await LoadWorldMapAsync();
        var fixture = Fixture(fixtureName);
        var cut = Render<MapPickRevealComponent>(parameters => parameters
            .Add(component => component.Card, Read<MapPickCard>(fixture, "card"))
            .Add(component => component.Reveal, Read<Quesshi.Kinds.MapPick.MapPickReveal>(fixture, "reveal"))
            .Add(component => component.Answers, StoredAnswers(fixture)));

        cut.WaitForAssertion(() => Assert.Contains(target, cut.Markup));
        Assert.Contains("Player 1", cut.Markup);
        Assert.Contains(playerAnswer, cut.Markup);
        Assert.Contains("Player 2", cut.Markup);
    }

    [Fact]
    public async Task World_map_keeps_successful_country_marks_and_editor_point_tolerance_visible()
    {
        await LoadWorldMapAsync();
        var world = Render<WorldMap>(parameters => parameters
            .Add(component => component.Marks, new[] { new MapMark(MapSelection.Country("DE"), "Player", true) }));

        world.WaitForAssertion(() => Assert.Contains("worldmap__country is-picked", world.Markup));

        var pointPayload = Read<MapPickPayload>(Fixture("point"), "payload");
        var editor = Render<MapPickEditor>(parameters => parameters
            .Add(component => component.Kind, "map_pick")
            .Add(component => component.Payload, pointPayload)
            .Add(component => component.Lang, "en")
            .Add(component => component.PayloadChanged, EventCallback.Factory.Create<object?>(this, _ => { })));

        Assert.Contains("worldmap__tolerance", editor.Markup);
    }

    [Fact]
    public async Task World_map_wraps_point_overlays_and_miss_lines_at_the_antimeridian()
    {
        await LoadWorldMapAsync();
        var world = Render<WorldMap>(parameters => parameters
            .Add(component => component.Target, MapSelection.Point(0, 179.9))
            .Add(component => component.TargetRadiusKm, 50d)
            .Add(component => component.Marks, new[]
            {
                new MapMark(MapSelection.Point(0, -179), "Wrong", false),
                new MapMark(MapSelection.Point(0, -179.9), "Right", true),
            }));

        Assert.Equal(2, world.FindAll(".worldmap__tolerance").Count);
        Assert.Equal(2, world.FindAll(".worldmap__pin--target").Count);
        var misses = world.FindAll(".worldmap__miss");
        Assert.Contains(misses, line => line.GetAttribute("x1") == "-179" && line.GetAttribute("x2") == "-180.1");
        Assert.Contains(misses, line => line.GetAttribute("x1") == "181" && line.GetAttribute("x2") == "179.9");
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

    [Fact]
    public void World_map_asset_parser_reads_every_country_from_the_shipped_asset()
    {
        var svg = ShippedWorldSvg();
        var countries = WorldMapAsset.Parse(svg);

        Assert.Equal(Regex.Matches(svg, "<path id=\"[A-Z]{2}\"").Count, countries.Count);
        Assert.NotEmpty(countries);
        Assert.Equal(WorldMapCountries.Codes.OrderBy(code => code), countries.Select(country => country.Iso).OrderBy(code => code));
        Assert.All(countries, country =>
        {
            Assert.StartsWith("M", country.Path);
            Assert.False(string.IsNullOrWhiteSpace(country.Name), $"{country.Iso} has no <title>.");
        });
    }

    [Theory]
    [InlineData("IR", "Iran")]
    [InlineData("NL", "Netherlands")]
    [InlineData("DE", "Germany")]
    public void Shipped_world_map_country_names_are_available_to_map_text(string iso, string expectedName)
    {
        var countries = WorldMapAsset.Parse(ShippedWorldSvg());
        var country = countries.Single(country => country.Iso == iso);

        Assert.Contains(expectedName, country.Name, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(country.Name, MapText.CountryName(countries, iso.ToLowerInvariant()));
    }

    [Fact]
    public void World_map_asset_parser_rejects_non_map_markup()
    {
        Assert.Empty(WorldMapAsset.Parse("<html><body><path id=\"nope\" d=\"M0,0Z\"/></body></html>"));
        Assert.Empty(WorldMapAsset.Parse(string.Empty));
    }

    [Fact]
    public async Task Map_text_editor_and_svg_attributes_remain_invariant_on_a_persian_thread()
    {
        await LoadWorldMapAsync();
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fa-IR");
            var selection = MapSelection.Point(52.37, 4.9);

            var label = MapText.Coordinates(selection);
            Assert.Equal("52.37°, 4.9°", label);
            Assert.Contains('.', label);
            Assert.DoesNotContain('٫', label);
            Assert.All(label.Where(char.IsDigit), character => Assert.True(char.IsAscii(character), $"'{character}' is not ASCII."));
            Assert.Equal(MapSelection.Country("DE"), MapSelection.Country("de"));

            var payload = Read<MapPickPayload>(Fixture("point"), "payload");
            var writes = new List<object?>();
            var editor = Render<MapPickEditor>(parameters => parameters
                .Add(component => component.Kind, "map_pick")
                .Add(component => component.Payload, payload)
                .Add(component => component.Lang, "en")
                .Add(component => component.PayloadChanged, EventCallback.Factory.Create<object?>(this, writes.Add)));

            Assert.Equal("0", editor.Find("[aria-label='Latitude']").GetAttribute("value"));
            Assert.Equal("179.9", editor.Find("[aria-label='Longitude']").GetAttribute("value"));
            await editor.InvokeAsync(() => editor.FindComponent<MapField>().Instance.SelectionChanged.InvokeAsync(MapSelection.Point(payload.Target!.Latitude!.Value, payload.Target.Longitude!.Value)));
            Assert.Equal(payload, Assert.IsType<MapPickPayload>(Assert.Single(writes)));

            var world = Render<WorldMap>(parameters => parameters
                .Add(component => component.Target, selection)
                .Add(component => component.TargetRadiusKm, 50d)
                .Add(component => component.Marks, new[] { new MapMark(selection, "Player", true) }));
            var attributes = world.FindAll("[cx], [cy], [rx], [ry]")
                .SelectMany(element => new[] { element.GetAttribute("cx"), element.GetAttribute("cy"), element.GetAttribute("rx"), element.GetAttribute("ry") })
                .Where(value => value is not null)
                .Cast<string>();
            Assert.NotEmpty(attributes);
            Assert.All(attributes, value =>
            {
                Assert.DoesNotContain('٫', value);
                Assert.All(value, character => Assert.True(char.IsAscii(character), $"'{character}' is not ASCII."));
            });
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Theory]
    [InlineData("basic")]
    [InlineData("point")]
    public async Task Editor_round_trips_a_fixture_payload_unchanged(string fixtureName)
    {
        await LoadWorldMapAsync();
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
        await LoadWorldMapAsync();
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
        await LoadWorldMapAsync();

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

    private Task LoadWorldMapAsync() =>
        new WorldMapAsset(Services.GetRequiredService<HttpClient>()).LoadAsync();

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

    private static string ShippedWorldSvg()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            var asset = Path.Combine(current.FullName, "platform", "src", "Quesshi.Shared", "maps", "world.svg");
            if (File.Exists(asset)) return File.ReadAllText(asset);
            current = current.Parent;
        }
        throw new FileNotFoundException("Could not find the shipped world map asset.");
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

    private sealed class PlayHost : ComponentBase
    {
        [Parameter, EditorRequired] public MapPickCard InitialCard { get; set; } = default!;
        [Parameter, EditorRequired] public MapPickCard NextCard { get; set; } = default!;
        [Parameter, EditorRequired] public EventCallback<object?> OnAnswer { get; set; }

        private MapPickCard? currentCard;

        protected override void OnParametersSet() => currentCard ??= InitialCard;

        public void MoveToNextCard()
        {
            currentCard = NextCard;
            StateHasChanged();
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MapPickPlay>(0);
            builder.AddAttribute(1, nameof(MapPickPlay.Card), currentCard);
            builder.AddAttribute(2, nameof(MapPickPlay.OnAnswer), OnAnswer);
            builder.CloseComponent();
        }
    }
}
