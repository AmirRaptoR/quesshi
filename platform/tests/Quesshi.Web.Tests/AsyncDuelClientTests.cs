using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Engine.Contracts;
using Quesshi.Formats.AsyncDuel;
using Quesshi.Shared;
using Quesshi.Web.Plugins;
using Quesshi.Web.Plugins.Formats.AsyncDuel;

namespace Quesshi.Web.Tests;

public sealed class AsyncDuelClientTests : BunitContext
{
    private readonly FakeApi api = new();
    private readonly FakeLanguage language = new();

    public AsyncDuelClientTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IClientTranslator>(new Words(language));
        Services.AddSingleton<IClientLanguageInfo>(language);
        Services.AddSingleton<IAsyncDuelSettingsApi>(api);
    }

    [Fact]
    public void The_registry_resolves_async_duel_to_a_client_whose_components_accept_the_format_parameters()
    {
        var registry = new ClientRegistry(new NoLoader());
        new WebPluginsEntry().RegisterFormats(registry);

        var client = Assert.IsType<AsyncDuelClient>(registry.ResolveFormat("async_duel"));

        ClientSlotComponent.EnsureAccepts(client.Settings, typeof(FormatSettingsParameters));
        ClientSlotComponent.EnsureAccepts(client.Screen, typeof(FormatScreenParameters));
        ClientSlotComponent.EnsureAccepts(client.Result, typeof(FormatScreenParameters));
    }

    [Fact]
    public void The_picker_offers_exactly_the_supported_question_counts()
    {
        var cut = RenderSettings();

        Assert.Equal(["10", "20", "30", "40", "50", "100"],
            cut.FindAll("[data-count]").Select(button => button.TextContent.Trim()));
    }

    [Fact]
    public void A_fourth_category_cannot_be_selected()
    {
        var cut = RenderSettings();

        foreach (var id in new[] { "c1", "c2", "c3", "c4" }) cut.Find($"[data-category='{id}']").Click();

        Assert.Equal(["c1", "c2", "c3"], cut.FindAll("[data-category].is-on").Select(chip => chip.GetAttribute("data-category")));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(40)]
    [InlineData(50)]
    [InlineData(100)]
    public async Task Chosen_settings_are_emitted_in_a_shape_the_async_duel_rules_accept(int count)
    {
        var cut = RenderSettings();
        cut.Find($"[data-count='{count}']").Click();
        cut.Find("[data-category='c1']").Click();
        cut.Find("[data-category='c2']").Click();
        cut.Find("[data-level='low']").Change("2");
        cut.Find("[data-level='high']").Change("4");
        cut.Find("#duel-capacity").Change("6");
        await cut.Find("[data-save-settings]").ClickAsync();

        var (sessionId, request) = Assert.Single(api.Saved);
        Assert.Equal("s1", sessionId);
        var format = new AsyncDuelFormat();
        var rules = format.ReadRules(request.Rules);
        var parameters = request.Content.GetProperty("parameters");
        var plan = format.Plan(new ContentSpec(request.Content.GetProperty("source").GetString()!, parameters, request.Content.GetProperty("count").GetInt32()), rules);
        Assert.Equal(count, plan.Count);
        Assert.Equal(["c1", "c2"], parameters.GetProperty("categories").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal([2, 3, 4], parameters.GetProperty("levels").EnumerateArray().Select(e => e.GetInt32()));
        Assert.InRange(request.Capacity!.Value, format.Spec.MinPlayers, format.Spec.MaxPlayers);
        Assert.Equal(6, request.Capacity);
    }

    [Fact]
    public async Task Saving_without_touching_capacity_emits_no_capacity()
    {
        var cut = RenderSettings();
        cut.Find("[data-count='20']").Click();
        await cut.Find("[data-save-settings]").ClickAsync();

        var (_, request) = Assert.Single(api.Saved);
        Assert.Null(request.Capacity);
    }

    [Fact]
    public void The_picker_renders_translated_text_right_to_left_for_an_rtl_language()
    {
        language.Code = "fa";
        language.Direction = "rtl";

        var cut = RenderSettings();

        Assert.Equal("rtl", cut.Find("[data-duel-settings]").GetAttribute("dir"));
        Assert.Contains("fa:home.length", cut.Markup);
        Assert.Contains("fa:home.categories", cut.Markup);
    }

    private IRenderedComponent<AsyncDuelSettings> RenderSettings() =>
        Render<AsyncDuelSettings>(p => p.Add(c => c.Parameters, new FormatSettingsParameters(Session(), IsOwner: true)));

    private static SessionView Session() => new(
        "s1", "ABCD", "async_duel", "lobby", "lobby", 0, 0, 1, 1, DateTimeOffset.UnixEpoch, null,
        new MySeat("p1", 1, "player", "active", 0, null, null), new CompleteRosterView([]),
        JsonSerializer.SerializeToElement(new { source = "bank", parameters = new { }, count = 10 }),
        JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { }));

    private sealed class FakeApi : IAsyncDuelSettingsApi
    {
        public List<(string Session, AsyncDuelSettingsRequest Request)> Saved { get; } = [];

        public bool PacksAvailable => false;

        public Task<IReadOnlyList<PickerPack>> PacksAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PickerPack>>([]);

        public Task<IReadOnlyList<PickerCategory>> CategoriesAsync(string language, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PickerCategory>>([.. new[] { "c1", "c2", "c3", "c4" }.Select(id => new PickerCategory(id, id, "🌍", "#2FBFB0"))]);

        public Task<string?> SaveAsync(string sessionId, AsyncDuelSettingsRequest request, CancellationToken cancellationToken = default)
        {
            Saved.Add((sessionId, request));
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class FakeLanguage : IClientLanguageInfo
    {
        public string Code { get; set; } = "en";
        public string Direction { get; set; } = "ltr";
        public string Num(object? value) => value?.ToString() ?? "";
    }

    private sealed class Words(FakeLanguage language) : IClientTranslator
    {
        public string this[string key] => $"{language.Code}:{key}";
    }

    private sealed class NoLoader : IClientAssemblyLoader
    {
        public Task<IReadOnlyList<System.Reflection.Assembly>> LoadAsync(string assemblyName) => throw new NotSupportedException();
    }
}
