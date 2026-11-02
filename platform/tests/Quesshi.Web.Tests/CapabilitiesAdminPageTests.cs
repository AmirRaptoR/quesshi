using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Web.Admin;
using Quesshi.Web.Plugins;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class CapabilitiesAdminPageTests : BunitContext
{
    private const string Capabilities = """
        [
          {"type":"switch","key":"sessions","entitled":true,"enabled":true,"settings":{"capacity":20}},
          {"type":"feature","key":"friends","entitled":true,"enabled":true,"settings":{}},
          {"type":"signin","key":"google","entitled":true,"enabled":true,"settings":{"clientId":"old.apps.googleusercontent.com"}}
        ]
        """;

    private static readonly Guid CategoryId = Guid.Parse("73f4dc75-cbac-4e93-b5e6-87841225a611");

    public CapabilitiesAdminPageTests()
    {
        Services.AddSingleton<IClientTranslator>(new Translator());
    }

    [Fact]
    public async Task Owner_turns_a_feature_capability_off_through_its_form()
    {
        var handler = new Handler(Session("Owner"), Json(Capabilities), Json("""{"token":"csrf"}"""), Response(HttpStatusCode.NoContent));
        var cut = await RenderAsync(handler);

        cut.WaitForState(() => cut.FindAll("#capability-feature-friends-enabled").Count == 1);
        cut.Find("#capability-feature-friends-enabled").Change(false);
        cut.Find("#capability-feature-friends").Submit();
        cut.WaitForState(() => handler.Requests.Contains((HttpMethod.Put, "/api/admin/capabilities/feature/friends")));

        using var body = JsonDocument.Parse(handler.Bodies.Last());
        Assert.False(body.RootElement.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void Every_registered_capability_with_settings_has_its_own_named_form()
    {
        var forms = new[] { ("switch", "sessions"), ("source", "bank"), ("signin", "google") }
            .Select(capability => CapabilityForms.For(capability.Item1, capability.Item2)!.Name)
            .ToArray();

        Assert.Equal(["SessionsCapabilityForm", "BankSourceCapabilityForm", "GoogleSignInCapabilityForm"], forms);
    }

    [Theory]
    [InlineData("switch", "guests", "GuestsCapabilityForm")]
    [InlineData("switch", "custom_questions", "CustomQuestionsCapabilityForm")]
    [InlineData("source", "pack", "PackSourceCapabilityForm")]
    [InlineData("feature", "matchmaking", "FeatureCapabilityForm")]
    [InlineData("feature", "stats", "FeatureCapabilityForm")]
    [InlineData("format", "poll", "FormatCapabilityForm")]
    public void Capabilities_without_settings_still_have_an_enabled_form(string type, string key, string form)
    {
        Assert.Equal(form, CapabilityForms.For(type, key)?.Name);
    }

    [Fact]
    public async Task Bank_form_lists_the_family_allow_lists_and_links_to_categories()
    {
        var handler = new Handler(
            Session("Owner"),
            Json("""[{"type":"source","key":"bank","entitled":true,"enabled":true,"settings":{"categoriesByFamily":{"trivia":["73f4dc75-cbac-4e93-b5e6-87841225a611"]}}}]"""),
            Json($$$"""[{"id":"{{{CategoryId}}}","slug":"history","names":{"en":"History"}}]"""));
        var cut = await RenderAsync(handler);

        cut.WaitForState(() => cut.FindAll("[data-testid='capability-bank-family-poll']").Count == 1);

        Assert.Equal(
            ("/admin/categories", true, false),
            (cut.Find("#capability-bank-categories-link").GetAttribute("href"),
             cut.Find("[data-testid='capability-bank-family-trivia'] input[type=checkbox]").HasAttribute("checked"),
             cut.Find("[data-testid='capability-bank-family-poll'] input[type=checkbox]").HasAttribute("checked")));
    }

    [Fact]
    public async Task Sessions_form_saves_the_edited_capacity()
    {
        var handler = new Handler(Session("Owner"), Json(Capabilities), Json("""{"token":"csrf"}"""), Response(HttpStatusCode.NoContent));
        var cut = await RenderAsync(handler);

        cut.WaitForState(() => cut.FindAll("#capability-sessions-capacity").Count == 1);
        cut.Find("#capability-sessions-capacity").Change("35");
        cut.Find("#capability-switch-sessions").Submit();
        cut.WaitForState(() => handler.Requests.Contains((HttpMethod.Put, "/api/admin/capabilities/switch/sessions")));

        using var body = JsonDocument.Parse(handler.Bodies.Last());
        Assert.Equal(35, body.RootElement.GetProperty("settings").GetProperty("capacity").GetInt32());
    }

    [Fact]
    public async Task Invalid_edit_shows_the_validator_message_on_its_form()
    {
        var handler = new Handler(
            Session("Owner"),
            Json(Capabilities),
            Json("""{"token":"csrf"}"""),
            Response(HttpStatusCode.BadRequest, """{"error":"invalid_settings","messages":["Capacity must be between 1 and 500."]}"""));
        var cut = await RenderAsync(handler);

        cut.WaitForState(() => cut.FindAll("#capability-sessions-capacity").Count == 1);
        cut.Find("#capability-sessions-capacity").Change("0");
        cut.Find("#capability-switch-sessions").Submit();
        cut.WaitForState(() => cut.FindAll("#capability-switch-sessions [data-testid='capability-messages']").Count == 1);

        Assert.Equal("Capacity must be between 1 and 500.", cut.Find("#capability-switch-sessions [data-testid='capability-messages'] li").TextContent);
    }

    [Fact]
    public async Task Moderator_cannot_open_capability_forms_and_navigation_excludes_the_role()
    {
        var handler = new Handler(Session("Moderator"));
        var registry = new AdminClientRegistry(new EmptyLoader(), new EmptyTranslationRegistry());
        new AdminCapabilitiesClientEntry().Register(registry);

        var cut = await RenderAsync(handler);

        Assert.Equal(
            (0, false, true),
            (cut.FindAll("form").Count,
             registry.Navigation.Single().IsVisibleTo(new AdminNavigationContext("Moderator", false)),
             registry.Navigation.Single().IsVisibleTo(new AdminNavigationContext("Owner", false))));
    }

    private async Task<IRenderedComponent<AdminCapabilities>> RenderAsync(Handler handler)
    {
        var client = new ClientHttp(new HttpClient(handler) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader());
        Services.AddSingleton(client);
        var state = new AdminState(new AdminApi(client));
        await state.InitializeAsync();
        return Render<AdminCapabilities>(parameters => parameters.AddCascadingValue(state));
    }

    private static HttpResponseMessage Session(string role) =>
        Json($$"""{"id":"21ac7193-52fd-4d4b-9194-5cd547448072","role":"{{role}}","isPlatform":false,"mustChangePassword":false}""");

    private static HttpResponseMessage Json(string json) => Response(HttpStatusCode.OK, json);

    private static HttpResponseMessage Response(HttpStatusCode status, string? json = null) => new(status) { Content = new StringContent(json ?? string.Empty, Encoding.UTF8, "application/json") };

    private sealed class Handler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);
        public List<(HttpMethod Method, string Path)> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery));
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return responses.Dequeue();
        }
    }

    private sealed class NullReloader : IClientReloader
    {
        public Task ReloadOnceAsync(string buildId) => Task.CompletedTask;
    }

    private sealed class EmptyLoader : IClientAssemblyLoader
    {
        public Task<IReadOnlyList<System.Reflection.Assembly>> LoadAsync(string assemblyName) => Task.FromResult<IReadOnlyList<System.Reflection.Assembly>>([]);
    }

    private sealed class EmptyTranslationRegistry : IClientTranslationRegistry
    {
        public void Register(ClientTranslation translation) { }
    }

    private sealed class Translator : IClientTranslator
    {
        public string this[string key] => key;
    }
}
