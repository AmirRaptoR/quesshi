using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Shared;
using Quesshi.Web.Plugins;
using Quesshi.Web.Plugins.Formats.Flashcards;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class FlashcardsClientTests : BunitContext
{
    private const string SessionId = "7f3c1d52-0000-4000-8000-0000000000aa";
    private const string PackId = "5a1c9f20-0000-4000-8000-0000000000bb";

    private readonly FakeCommands commands = new();
    private readonly FakeServer server = new();

    public FlashcardsClientTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new ClientHttp(new HttpClient(server) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader());
        var translator = new Translator(http);
        translator.UseAsync("en").GetAwaiter().GetResult();
        var registry = new ClientRegistry(new NoLoader());
        var entry = new WebPluginsEntry();
        entry.Register(registry);
        entry.RegisterFormats(registry);
        Services.AddSingleton<IClientRegistry>(registry);
        Services.AddSingleton(http);
        Services.AddSingleton(translator);
        Services.AddSingleton<IClientTranslator>(translator);
        Services.AddSingleton<IFormatCommands>(commands);
    }

    [Fact]
    public void The_entry_registers_the_flashcards_format_client_that_starts_from_its_settings()
    {
        var client = Services.GetRequiredService<IClientRegistry>() is IFormatClientRegistry formats ? formats.ResolveFormat("flashcards") : null;

        Assert.IsType<FlashcardsClient>(client);
        Assert.True(client.StartsFromSettings);
    }

    [Fact]
    public async Task Settings_lists_the_players_packs_and_creates_the_session_from_the_chosen_one()
    {
        var cut = Render<FlashcardsSettings>(p => p.Add(c => c.Parameters, new FormatSettingsParameters(View(Body("front", 0)), true)));
        cut.WaitForAssertion(() => Assert.Equal(["Capitals", "Verbs"], cut.FindAll("[data-flashcards-pack] option").Select(o => o.TextContent)));

        cut.Find("[data-flashcards-pack]").Change(server.VerbsId);
        await cut.Find("[data-flashcards-start]").ClickAsync();

        var created = Assert.Single(server.Created);
        Assert.Equal("flashcards", created.GetProperty("format").GetString());
        Assert.Equal("pack", created.GetProperty("content").GetProperty("source").GetString());
        Assert.Equal(server.VerbsId, created.GetProperty("content").GetProperty("parameters").GetProperty("packId").GetString());
        Assert.Equal(3, created.GetProperty("content").GetProperty("count").GetInt32());
        Assert.Equal($"/s/{created.GetProperty("id").GetGuid()}", new Uri(Services.GetRequiredService<NavigationManager>().Uri).AbsolutePath);
    }

    [Fact]
    public void On_the_front_the_screen_shows_the_front_and_a_flip_control_but_neither_the_back_nor_ratings()
    {
        var cut = Render<FlashcardsScreen>(p => p.Add(c => c.Parameters, new FormatScreenParameters(View(Body("front", 2)))));

        Assert.Equal("Capital of France?", cut.Find("[data-flashcards-front]").TextContent);
        Assert.Single(cut.FindAll("[data-flashcards-flip]"));
        Assert.Empty(cut.FindAll("[data-flashcards-card] [data-rating]"));
        Assert.Empty(cut.FindAll("[data-flashcard-back]"));
        Assert.DoesNotContain("Paris", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Flipping_sends_the_flip_action()
    {
        var cut = Render<FlashcardsScreen>(p => p.Add(c => c.Parameters, new FormatScreenParameters(View(Body("front", 2)))));

        await cut.Find("[data-flashcards-flip]").ClickAsync();

        Assert.Equal(["flip"], commands.Actions);
    }

    [Fact]
    public async Task After_a_flip_the_screen_shows_the_back_and_four_ratings_and_pressing_one_sends_it()
    {
        var cut = Render<FlashcardsScreen>(p => p.Add(c => c.Parameters, new FormatScreenParameters(View(Body("back", 2, "Paris")))));

        cut.WaitForElement("[data-flashcards-card] [data-rating]");
        Assert.Equal("Paris", cut.Find("[data-flashcard-back]").TextContent);
        Assert.Equal(["0", "1", "2", "3"], cut.FindAll("[data-flashcards-card] button[data-rating]").Select(b => b.TextContent));
        Assert.Empty(cut.FindAll("[data-flashcards-flip]"));

        await cut.InvokeAsync(() => cut.Find("button[data-rating='3']").ClickAsync());

        var sent = Assert.Single(commands.Sent);
        Assert.Equal("answer", sent.Type);
        Assert.Equal("""{"rating":3}""", ((JsonElement)sent.Payload!).GetRawText());
    }

    [Fact]
    public void The_screen_shows_progress_through_the_deck()
    {
        var cut = Render<FlashcardsScreen>(p => p.Add(c => c.Parameters, new FormatScreenParameters(View(Body("front", 2, total: 10)))));

        Assert.Equal("Card 3 of 10", cut.Find("[data-flashcards-progress]").TextContent);
        Assert.Equal("2", cut.Find("[data-flashcards-bar]").GetAttribute("value"));
        Assert.Equal("10", cut.Find("[data-flashcards-bar]").GetAttribute("max"));
    }

    [Fact]
    public void The_result_shows_the_count_for_each_rating_including_zeros()
    {
        var cut = Render<FlashcardsResult>(p => p.Add(c => c.Parameters, new FormatScreenParameters(FinishedView([4, 0, 1, 0]))));

        Assert.Equal(["0:4", "1:0", "2:1", "3:0"],
            cut.FindAll("[data-flashcards-counts] li").Select(li => $"{li.GetAttribute("data-rating")}:{li.GetAttribute("data-count")}"));
        Assert.Equal("0", cut.Find("li[data-rating='1'] strong").TextContent);
    }

    [Fact]
    public async Task Study_again_creates_a_new_session_from_the_same_pack()
    {
        var cut = Render<FlashcardsResult>(p => p.Add(c => c.Parameters, new FormatScreenParameters(FinishedView([1, 1, 1, 0]))));

        await cut.Find("[data-flashcards-again]").ClickAsync();

        var created = Assert.Single(server.Created);
        Assert.Equal("flashcards", created.GetProperty("format").GetString());
        Assert.Equal(PackId, created.GetProperty("content").GetProperty("parameters").GetProperty("packId").GetString());
        Assert.Equal(3, created.GetProperty("content").GetProperty("count").GetInt32());
        Assert.NotEqual(SessionId, created.GetProperty("id").GetString());
        Assert.Equal($"/s/{created.GetProperty("id").GetGuid()}", new Uri(Services.GetRequiredService<NavigationManager>().Uri).AbsolutePath);
    }

    private static JsonObject Body(string side, int slot, string? back = null, int total = 10) => new()
    {
        ["slot"] = slot,
        ["phase"] = side == "front" ? 0 : 1,
        ["side"] = side,
        ["card"] = JsonNode.Parse("""{"front":"Capital of France?"}"""),
        ["back"] = back is null ? null : JsonNode.Parse($$"""{"back":"{{back}}"}"""),
        ["total"] = total,
        ["rated"] = slot,
        ["kind"] = "flashcard",
        ["kindVersion"] = 1,
    };

    private static SessionView View(JsonObject body, string status = "running", JsonObject? content = null) =>
        PlatformState.ParseView(new JsonObject
        {
            ["id"] = SessionId, ["code"] = "ABCD23", ["format"] = "flashcards", ["status"] = status,
            ["phase"] = "0", ["slot"] = body["slot"]?.GetValue<int>() ?? 0, ["step"] = 0, ["version"] = 1, ["configVersion"] = 1,
            ["now"] = "2026-01-01T00:00:00Z", ["deadline"] = null,
            ["me"] = new JsonObject { ["id"] = "amir", ["seat"] = 0, ["role"] = "player", ["status"] = "active", ["score"] = 0, ["place"] = null, ["outcome"] = null },
            ["roster"] = new JsonObject { ["total"] = 1, ["seats"] = new JsonArray() },
            ["content"] = content ?? new JsonObject { ["count"] = 10 },
            ["rules"] = new JsonObject(),
            ["body"] = body,
        }.ToJsonString())!;

    private static SessionView FinishedView(int[] counts) => View(
        new JsonObject { ["counts"] = new JsonArray(counts.Select(c => (JsonNode)c).ToArray()), ["total"] = 3 },
        "finished",
        JsonNode.Parse($$"""{"source":"pack","parameters":{"packId":"{{PackId}}"},"count":3}""")!.AsObject());

    private sealed class FakeCommands : IFormatCommands
    {
        public List<(string Type, object? Payload)> Sent { get; } = [];
        public List<string> Actions { get; } = [];

        public Task<bool> SendAsync(string type, object? payload = null, CancellationToken cancellationToken = default)
        {
            Sent.Add((type, payload));
            return Task.FromResult(true);
        }

        public Task<bool> SendAsync(string type, object? payload, int revision, CancellationToken cancellationToken = default) => SendAsync(type, payload, cancellationToken);

        public Task<bool> ActionAsync(string name, CancellationToken cancellationToken = default)
        {
            Actions.Add(name);
            return Task.FromResult(true);
        }
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        public string VerbsId { get; } = "5a1c9f20-0000-4000-8000-0000000000cc";
        public List<JsonElement> Created { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            if (path.StartsWith("/i18n/", StringComparison.Ordinal))
            {
                var file = Path.Combine(AppContext.BaseDirectory, "i18n", Path.GetFileName(path));
                return File.Exists(file) ? Json(File.ReadAllText(file)) : new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return path switch
            {
                "/api/packs" => Json($$"""[{"id":"{{PackId}}","title":"Capitals","description":null,"language":"en","visibility":"private"},{"id":"{{VerbsId}}","title":"Verbs","description":null,"language":"en","visibility":"private"}]"""),
                "/api/antiforgery" => Json("""{"token":"csrf"}"""),
                "/api/sessions" when request.Method == HttpMethod.Post => Record(body),
                _ when path.EndsWith("/questions", StringComparison.Ordinal) => Json("""[{"id":"1"},{"id":"2"},{"id":"3"}]"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private HttpResponseMessage Record(string body)
        {
            Created.Add(JsonDocument.Parse(body).RootElement.Clone());
            return Json("""{"id":"x","code":"ABCD"}""", HttpStatusCode.Created);
        }

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class NullReloader : IClientReloader
    {
        public Task ReloadOnceAsync(string buildId) => Task.CompletedTask;
    }

    private sealed class NoLoader : IClientAssemblyLoader
    {
        public Task<IReadOnlyList<System.Reflection.Assembly>> LoadAsync(string assemblyName) => throw new NotSupportedException();
    }
}
