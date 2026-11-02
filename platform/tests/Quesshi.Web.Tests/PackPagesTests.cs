using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Shared;
using Quesshi.Web.Pages;
using Quesshi.Web.Plugins;
using Quesshi.Web.Plugins.Formats.AsyncDuel;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class PackPagesTests : BunitContext
{
    private readonly PackServer server = new();

    public PackPagesTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    [Fact]
    public void My_packs_lists_the_signed_in_players_packs()
    {
        server.AddPack("Capitals");
        server.AddPack("Rivers");

        var cut = RenderMyPacks();

        Assert.Equal(["Capitals", "Rivers"], cut.FindAll("[data-pack] [data-field='title']").Select(e => e.TextContent));
    }

    [Fact]
    public async Task My_packs_creates_a_pack_and_lists_it()
    {
        var cut = RenderMyPacks();
        Assert.NotNull(cut.Find("[data-test='packs-empty']"));

        cut.Find("[data-test='pack-title']").Input("Capitals");
        await cut.Find("[data-test='pack-create']").ClickAsync();

        Assert.Equal(["Capitals"], cut.FindAll("[data-pack] [data-field='title']").Select(e => e.TextContent));
        Assert.Equal(["Capitals"], server.Packs.Select(pack => pack.Title));
    }

    [Fact]
    public async Task My_packs_deletes_a_pack_from_the_page_and_the_server()
    {
        var keep = server.AddPack("Keep");
        var drop = server.AddPack("Drop");
        var cut = RenderMyPacks();

        await cut.Find($"[data-pack='{drop}'] [data-action='delete']").ClickAsync();

        Assert.Equal([keep], cut.FindAll("[data-pack]").Select(e => Guid.Parse(e.GetAttribute("data-pack")!)));
        Assert.Equal([keep], server.Packs.Select(pack => pack.Id));
    }

    [Fact]
    public void The_editor_renders_a_kind_editor_for_each_question()
    {
        var pack = server.AddPack("Mixed");
        server.AddQuestion(pack, "choice", "Capital of France?");
        server.AddQuestion(pack, "order", "Sort these");

        var cut = RenderEditor(pack);

        Assert.Equal(["choice", "order"], cut.FindAll("[data-question] [data-kind]").Select(e => e.GetAttribute("data-kind")));
    }

    [Fact]
    public async Task The_editor_adds_questions_of_two_kinds()
    {
        var pack = server.AddPack("Mixed");
        var cut = RenderEditor(pack);

        cut.Find("[data-add-kind='choice']").Click();
        cut.Find("[data-question='new'] [data-field='prompt']").Input("Capital of France?");
        cut.Find("[data-question='new'] [data-option='0']").Input("Paris");
        cut.Find("[data-question='new'] [data-option='1']").Input("Rome");
        cut.Find("[data-question='new'] [data-payload-save]").Click();
        await cut.Find("[data-question='new'] [data-action='save']").ClickAsync();

        cut.Find("[data-add-kind='order']").Click();
        cut.Find("[data-question='new'] [data-field='prompt']").Input("Smallest first");
        cut.Find("[data-question='new'] [data-item-text='0']").Input("Mouse");
        cut.Find("[data-question='new'] [data-item-text='1']").Input("Whale");
        cut.Find("[data-question='new'] [data-payload-save]").Click();
        await cut.Find("[data-question='new'] [data-action='save']").ClickAsync();

        Assert.Equal(["choice", "order"], server.Questions(pack).Select(question => question.Kind));
        Assert.Equal(["Capital of France?", "Smallest first"], server.Questions(pack).Select(question => question.Prompt));
        Assert.Equal(["choice", "order"], cut.FindAll("[data-question] [data-kind]").Select(e => e.GetAttribute("data-kind")));
        Assert.Empty(cut.FindAll("[data-question='new']"));
    }

    [Fact]
    public async Task Reordering_questions_persists_after_reloading_the_editor()
    {
        var pack = server.AddPack("Ordered");
        var first = server.AddQuestion(pack, "choice", "First");
        var second = server.AddQuestion(pack, "choice", "Second");
        var cut = RenderEditor(pack);

        await cut.Find($"[data-question='{second}'] [data-action='up']").ClickAsync();

        Assert.Equal([second, first], cut.FindAll("[data-question]").Select(e => Guid.Parse(e.GetAttribute("data-question")!)));
        var reloaded = RenderEditor(pack);
        Assert.Equal([second, first], reloaded.FindAll("[data-question]").Select(e => Guid.Parse(e.GetAttribute("data-question")!)));
        Assert.Equal(["Second", "First"], reloaded.FindAll("[data-field='prompt']").Select(e => e.GetAttribute("value")));
    }

    [Fact]
    public async Task Removing_a_question_deletes_it_from_the_pack()
    {
        var pack = server.AddPack("Ordered");
        var first = server.AddQuestion(pack, "choice", "First");
        server.AddQuestion(pack, "choice", "Second");
        var cut = RenderEditor(pack);

        await cut.Find($"[data-question='{first}'] [data-action='remove']").ClickAsync();

        Assert.Equal(["Second"], server.Questions(pack).Select(question => question.Prompt));
        Assert.Single(cut.FindAll("[data-question]"));
    }

    [Fact]
    public async Task Changing_visibility_updates_the_pack()
    {
        var pack = server.AddPack("Shared");
        var cut = RenderEditor(pack);

        await cut.Find("[data-test='pack-visibility']").ChangeAsync(new ChangeEventArgs { Value = "unlisted" });

        Assert.Equal("unlisted", server.Packs.Single().Visibility);
    }

    [Fact]
    public void The_lobby_settings_offer_pack_as_a_source_with_a_picker_when_custom_questions_is_on()
    {
        server.AddPack("Capitals");
        server.AddPack("Rivers");

        var cut = RenderSettings();

        Assert.Equal(["bank", "pack"], cut.FindAll("[data-source]").Select(e => e.GetAttribute("data-source")));
        cut.Find("[data-source='pack']").Click();
        Assert.Equal(["Capitals", "Rivers"], cut.FindAll("[data-pack-picker] option").Select(e => e.TextContent));
    }

    [Fact]
    public async Task Saving_a_picked_pack_sends_the_pack_source_with_its_id()
    {
        var capitals = server.AddPack("Capitals");
        var rivers = server.AddPack("Rivers");
        var cut = RenderSettings();

        cut.Find("[data-source='pack']").Click();
        cut.Find("[data-pack-picker]").Change(rivers.ToString());
        await cut.Find("[data-save-settings]").ClickAsync();

        var content = server.SavedSettings.Single().GetProperty("content");
        Assert.Equal("pack", content.GetProperty("source").GetString());
        Assert.Equal(rivers.ToString(), content.GetProperty("parameters").GetProperty("packId").GetString());
        Assert.NotEqual(capitals, rivers);
    }

    [Fact]
    public void The_lobby_settings_do_not_offer_pack_when_custom_questions_is_off()
    {
        server.CustomQuestions = false;
        server.AddPack("Capitals");

        var cut = RenderSettings();

        Assert.Empty(cut.FindAll("[data-source]"));
        Assert.Empty(cut.FindAll("[data-pack-picker]"));
    }

    [Fact]
    public void A_guest_sees_neither_the_my_packs_entry_nor_the_pack_source()
    {
        server.Guest = true;

        var home = RenderHome();
        var settings = RenderSettings();

        Assert.Empty(home.FindAll("[data-test='my-packs-link']"));
        Assert.Empty(settings.FindAll("[data-source]"));
        Assert.Empty(settings.FindAll("[data-pack-picker]"));
    }

    [Fact]
    public void A_signed_in_player_sees_the_my_packs_entry()
    {
        Assert.NotEmpty(RenderHome().FindAll("[data-test='my-packs-link']"));
    }

    [Fact]
    public void The_my_packs_entry_is_hidden_when_custom_questions_is_off()
    {
        server.CustomQuestions = false;

        Assert.Empty(RenderHome().FindAll("[data-test='my-packs-link']"));
    }

    private IRenderedComponent<PackEditor> RenderEditor(Guid id)
    {
        Wire();
        return Render<PackEditor>(p => p.Add(c => c.Id, id));
    }

    private IRenderedComponent<MyPacks> RenderMyPacks()
    {
        Wire();
        return Render<MyPacks>();
    }

    private IRenderedComponent<Home> RenderHome()
    {
        Wire();
        return Render<Home>();
    }

    private IRenderedComponent<AsyncDuelSettings> RenderSettings()
    {
        Wire();
        return Render<AsyncDuelSettings>(p => p.Add(c => c.Parameters, new FormatSettingsParameters(Session(), IsOwner: true)));
    }

    private bool wired;

    private void Wire()
    {
        if (wired) return;
        wired = true;
        var http = new ClientHttp(new HttpClient(server) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader());
        var config = new AppConfig(http, JSInterop.JSRuntime);
        config.LoadAsync().GetAwaiter().GetResult();
        var translator = new Translator(http);
        translator.UseAsync("en").GetAwaiter().GetResult();
        var api = new PlayerApi(http);
        var auth = new AuthState(api);
        auth.InitializeAsync().GetAwaiter().GetResult();
        var access = new PackAvailability(config, auth);
        var packApi = new PackApi(api);
        Services.AddSingleton(config);
        Services.AddSingleton(translator);
        Services.AddSingleton(auth);
        Services.AddSingleton(api);
        Services.AddSingleton(access);
        Services.AddSingleton(packApi);
        Services.AddSingleton(new PlatformState(config));
        Services.AddSingleton<IClientTranslator>(translator);
        Services.AddSingleton<IClientLanguageInfo>(new ClientLanguageInfo(translator, config));
        Services.AddSingleton<IAsyncDuelSettingsApi>(new AsyncDuelSettingsApi(api, access, packApi));
        var registry = new ClientRegistry(new NoLoader());
        new WebPluginsEntry().Register(registry);
        Services.AddSingleton<IClientRegistry>(registry);
        Services.AddSingleton<IFormatClientRegistry>(registry);
    }

    private static SessionView Session() => new(
        "s1", "ABCD", "async_duel", "lobby", "lobby", 0, 0, 1, 1, DateTimeOffset.UnixEpoch, null,
        new MySeat("p1", 1, "player", "active", 0, null, null), new CompleteRosterView([]),
        JsonSerializer.SerializeToElement(new { source = "bank", parameters = new { }, count = 10 }),
        JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(new { }));

    private sealed class NoLoader : IClientAssemblyLoader
    {
        public Task<IReadOnlyList<System.Reflection.Assembly>> LoadAsync(string assemblyName) => throw new NotSupportedException();
    }

    private sealed class NullReloader : IClientReloader
    {
        public Task ReloadOnceAsync(string buildId) => Task.CompletedTask;
    }

    private sealed record StoredPack(Guid Id, string Title, string Visibility);
    private sealed record StoredQuestion(Guid Id, string Kind, string Prompt, JsonElement Payload);

    /// <summary>A stateful stand-in for <c>/api/packs</c> that keeps packs, questions and their order between page loads.</summary>
    private sealed class PackServer : HttpMessageHandler
    {
        private readonly List<StoredPack> packs = [];
        private readonly Dictionary<Guid, List<StoredQuestion>> questions = [];
        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

        public bool CustomQuestions { get; set; } = true;
        public bool Guest { get; set; }
        public List<JsonElement> SavedSettings { get; } = [];
        public IReadOnlyList<StoredPack> Packs => packs;
        public IReadOnlyList<StoredQuestion> Questions(Guid pack) => questions[pack];

        public Guid AddPack(string title)
        {
            var pack = new StoredPack(Guid.NewGuid(), title, "private");
            packs.Add(pack);
            questions[pack.Id] = [];
            return pack.Id;
        }

        public Guid AddQuestion(Guid pack, string kind, string prompt)
        {
            var payload = kind == "choice"
                ? JsonSerializer.SerializeToElement(new { options = new[] { "a", "b" }, correctIndex = 0 }, Web)
                : JsonSerializer.SerializeToElement(new { items = new[] { new { id = "one", text = "1" }, new { id = "two", text = "2" } } }, Web);
            var question = new StoredQuestion(Guid.NewGuid(), kind, prompt, payload);
            questions[pack].Add(question);
            return question.Id;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            JsonElement? body = request.Content is null ? null : JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            switch (path)
            {
                case "/api/config":
                    var switches = CustomQuestions ? "[\"custom_questions\"]" : "[]";
                    return Json($$"""{"name":"Tenant","languages":[{"code":"en","displayName":"English","direction":"ltr"}],"defaultLanguage":"en","capabilities":{"format":[],"source":["bank","pack"],"switch":{{switches}}},"signInMethods":[],"configVersion":1,"serverBuildId":"test"}""");
                case "/api/me":
                    return Json($$"""{"id":"{{Guid.Empty}}","displayName":"Player","avatarSeed":"a","lang":"en","isGuest":{{(Guest ? "true" : "false")}}}""");
                case "/api/antiforgery":
                    return Json("""{"token":"csrf"}""");
                case "/api/me/inbox":
                    return Json("""{"version":1,"invitations":[],"lobbies":[],"turns":[],"items":[]}""");
                case "/api/packs" when request.Method == HttpMethod.Get:
                    return Json(JsonSerializer.Serialize(packs.Select(Summary), Web));
                case "/api/packs" when request.Method == HttpMethod.Post:
                    var created = new StoredPack(Guid.NewGuid(), body!.Value.GetProperty("title").GetString()!, "private");
                    packs.Add(created);
                    questions[created.Id] = [];
                    return Json(JsonSerializer.Serialize(Summary(created), Web), HttpStatusCode.Created);
            }

            if (segments is ["api", "sessions", _, "settings"])
            {
                SavedSettings.Add(body!.Value);
                return Json("{}");
            }

            if (segments is ["api", "packs", var rawId, ..] && Guid.TryParse(rawId, out var id) && packs.FindIndex(pack => pack.Id == id) is var index and >= 0)
            {
                var list = questions[id];
                switch (segments.Length, request.Method.Method)
                {
                    case (3, "GET"):
                        return Json(JsonSerializer.Serialize(Summary(packs[index]), Web));
                    case (3, "PUT"):
                        if (body!.Value.TryGetProperty("visibility", out var visibility) && visibility.ValueKind == JsonValueKind.String)
                            packs[index] = packs[index] with { Visibility = visibility.GetString()! };
                        return Json(JsonSerializer.Serialize(Summary(packs[index]), Web));
                    case (3, "DELETE"):
                        packs.RemoveAt(index);
                        questions.Remove(id);
                        return new HttpResponseMessage(HttpStatusCode.NoContent);
                    case (4, "GET"):
                        return Json(JsonSerializer.Serialize(list.Select((q, position) => Question(q, position)), Web));
                    case (4, "POST"):
                        var added = new StoredQuestion(Guid.NewGuid(), body!.Value.GetProperty("kind").GetString()!, body.Value.GetProperty("prompt").GetString()!, body.Value.GetProperty("payload"));
                        list.Add(added);
                        return Json(JsonSerializer.Serialize(Question(added, list.Count - 1), Web), HttpStatusCode.Created);
                    case (5, "PUT") when segments[4] == "order":
                        var order = body!.Value.GetProperty("questionIds").EnumerateArray().Select(e => e.GetGuid()).ToList();
                        if (order.Count != list.Count || order.Except(list.Select(q => q.Id)).Any()) return Json("""{"error":"invalid_order"}""", HttpStatusCode.BadRequest);
                        var sorted = order.Select(qid => list.Single(q => q.Id == qid)).ToList();
                        list.Clear();
                        list.AddRange(sorted);
                        return new HttpResponseMessage(HttpStatusCode.NoContent);
                    case (5, "PUT"):
                        var qid2 = Guid.Parse(segments[4]);
                        var at = list.FindIndex(q => q.Id == qid2);
                        list[at] = list[at] with { Prompt = body!.Value.GetProperty("prompt").GetString()!, Payload = body.Value.GetProperty("payload") };
                        return Json(JsonSerializer.Serialize(Question(list[at], at), Web));
                    case (5, "DELETE"):
                        list.RemoveAll(q => q.Id == Guid.Parse(segments[4]));
                        return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
            }

            return path.StartsWith("/i18n/", StringComparison.Ordinal) ? Json("{}") : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }

        private static object Summary(StoredPack pack) => new { pack.Id, pack.Title, Description = (string?)null, Language = "en", pack.Visibility };

        private static object Question(StoredQuestion question, int position) => new
        {
            question.Id, Position = position, question.Kind, KindVersion = 1, Family = "trivia", Language = "en", Level = 1,
            question.Prompt, question.Payload, Explanation = (string?)null,
        };

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
