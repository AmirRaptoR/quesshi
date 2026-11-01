using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Web.Admin;
using Quesshi.Web.Plugins;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class BrandingAdminPageTests : BunitContext
{
    private const string EmptyBranding = """{"palette":{},"logoMediaId":null,"logoUrl":null,"iconMediaId":null,"iconUrl":null,"appNames":{},"landingTexts":{}}""";
    private const string Languages = """[{"code":"en"},{"code":"fa"}]""";
    private static readonly Guid AssetId = Guid.Parse("73f4dc75-cbac-4e93-b5e6-87841225a611");

    public BrandingAdminPageTests()
    {
        Services.AddSingleton<IClientTranslator>(new Translator());
    }

    [Fact]
    public async Task Owner_branding_screen_puts_a_theme_colour()
    {
        var handler = new Handler(
            Response(HttpStatusCode.OK, SessionJson("Owner")),
            Response(HttpStatusCode.OK, EmptyBranding),
            Response(HttpStatusCode.OK, Languages),
            Response(HttpStatusCode.OK, """{"token":"csrf"}"""),
            Response(HttpStatusCode.OK, EmptyBranding));
        var cut = await RenderAsync(handler);

        cut.WaitForState(() => cut.FindAll("#branding-color-firouzeh").Count == 1);
        cut.Find("#branding-color-firouzeh").Change("#112233");
        cut.Find("#branding-save").Click();
        cut.WaitForState(() => handler.Requests.Contains((HttpMethod.Put, "/api/admin/branding")));

        Assert.Contains("\"firouzeh\":\"#112233\"", handler.Bodies.Single(body => body.Contains("firouzeh", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Owner_branding_screen_puts_per_language_app_name_and_landing_text()
    {
        var handler = new Handler(
            Response(HttpStatusCode.OK, SessionJson("Owner")),
            Response(HttpStatusCode.OK, EmptyBranding),
            Response(HttpStatusCode.OK, Languages),
            Response(HttpStatusCode.OK, """{"token":"csrf"}"""),
            Response(HttpStatusCode.OK, EmptyBranding));
        var cut = await RenderAsync(handler);

        cut.WaitForState(() => cut.FindAll("#branding-app-name-fa").Count == 1);
        cut.Find("#branding-app-name-fa").Change("کوییشی");
        cut.Find("#branding-landing-fa").Change("خوش آمدید");
        cut.Find("#branding-save").Click();
        cut.WaitForState(() => handler.Requests.Contains((HttpMethod.Put, "/api/admin/branding")));

        using var body = JsonDocument.Parse(handler.Bodies.Last(item => item.Contains("appNames", StringComparison.Ordinal)));
        Assert.Equal(
            ("کوییشی", "خوش آمدید"),
            (body.RootElement.GetProperty("appNames").GetProperty("fa").GetString(), body.RootElement.GetProperty("landingTexts").GetProperty("fa").GetString()));
    }

    [Fact]
    public async Task Owner_selects_logo_media_and_the_save_carries_its_id()
    {
        var handler = MediaHandler();
        var cut = await RenderAsync(handler);

        SelectMedia(cut, "branding-logo", handler);

        Assert.Contains($"\"logoMediaId\":\"{AssetId}\"", handler.Bodies.Last(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Owner_selects_icon_media_and_the_save_carries_its_id()
    {
        var handler = MediaHandler();
        var cut = await RenderAsync(handler);

        SelectMedia(cut, "branding-icon", handler);

        Assert.Contains($"\"iconMediaId\":\"{AssetId}\"", handler.Bodies.Last(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Moderator_cannot_open_branding_controls_and_navigation_excludes_the_role()
    {
        var handler = new Handler(Response(HttpStatusCode.OK, SessionJson("Moderator")));
        var registry = new AdminClientRegistry(new EmptyLoader(), new EmptyTranslationRegistry());
        new BrandingAdminClientEntry().Register(registry);

        var cut = await RenderAsync(handler);

        Assert.Equal((0, false), (cut.FindAll("form").Count, registry.Navigation.Single().IsVisibleTo(new AdminNavigationContext("Moderator", false))));
    }

    private static Handler MediaHandler() => new(
        Response(HttpStatusCode.OK, SessionJson("Owner")),
        Response(HttpStatusCode.OK, EmptyBranding),
        Response(HttpStatusCode.OK, Languages),
        Response(HttpStatusCode.OK, """{"token":"csrf"}"""),
        Response(HttpStatusCode.Created, $$"""{"id":"{{AssetId}}","hash":"hash","kind":"image","mime":"image/png","bytes":10,"meta":{},"attribution":null,"url":"/media/hash"}"""),
        Response(HttpStatusCode.OK, EmptyBranding));

    private static void SelectMedia(IRenderedComponent<AdminBranding> cut, string section, Handler handler)
    {
        cut.WaitForState(() => cut.FindAll($"[data-testid='{section}'] #media-url").Count == 1);
        var chooser = cut.Find($"[data-testid='{section}']");
        chooser.QuerySelector("#media-url")!.Change("https://example.test/picture.png");
        chooser.QuerySelector("form")!.Submit();
        cut.WaitForState(() => cut.FindAll($"[data-testid='{section}'] [data-testid='selected-media']").Count == 1);
        cut.Find("#branding-save").Click();
        cut.WaitForState(() => handler.Requests.Contains((HttpMethod.Put, "/api/admin/branding")));
    }

    private async Task<IRenderedComponent<AdminBranding>> RenderAsync(Handler handler)
    {
        var client = new ClientHttp(new HttpClient(handler) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader());
        Services.AddSingleton(client);
        var state = new AdminState(new AdminApi(client));
        await state.InitializeAsync();
        return Render<AdminBranding>(parameters => parameters.AddCascadingValue(state));
    }

    private static string SessionJson(string role) => $$"""{"id":"21ac7193-52fd-4d4b-9194-5cd547448072","role":"{{role}}","isPlatform":false,"mustChangePassword":false}""";
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
