using System.Net;
using System.Text;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Web.Admin;
using Quesshi.Web.Plugins;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class MediaChooserTests : BunitContext
{
    public MediaChooserTests()
    {
        Services.AddSingleton<IClientTranslator>(new MediaTranslator());
        Services.AddSingleton<IAdminClientRegistry>(new AdminClientRegistry(new EmptyLoader(), new EmptyTranslationRegistry()));
    }

    [Fact]
    public async Task Editor_upload_selects_the_stored_asset_id()
    {
        var id = Guid.Parse("73f4dc75-cbac-4e93-b5e6-87841225a611");
        var handler = new Handler(
            Response(HttpStatusCode.OK, SessionJson("Editor")),
            Response(HttpStatusCode.OK, """{"token":"csrf"}"""),
            Response(HttpStatusCode.Created, AssetJson(id)));
        var state = await StateAsync(handler);
        Guid? selected = null;
        var cut = Render<MediaChooser>(parameters => parameters
            .AddCascadingValue(state)
            .Add(component => component.ValueChanged, EventCallback.Factory.Create<Guid?>(this, value => selected = value)));

        cut.Find("#media-attribution").Change("Museum collection");
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(PngBytes(), "photo.png", null, "image/png"));

        cut.WaitForAssertion(() => Assert.Equal(id, selected));

        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.Path == "/api/admin/media");
        Assert.Contains("Museum collection", handler.Bodies.Single(body => body.Contains("Museum collection", StringComparison.Ordinal)));
        Assert.Contains("/media/hash", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Editor_upload_larger_than_the_client_limit_shows_a_localized_error_without_posting_or_selecting()
    {
        var handler = new Handler(Response(HttpStatusCode.OK, SessionJson("Editor")));
        var state = await StateAsync(handler);
        Guid? selected = null;
        var cut = Render<MediaChooser>(parameters => parameters
            .AddCascadingValue(state)
            .Add(component => component.ValueChanged, EventCallback.Factory.Create<Guid?>(this, value => selected = value)));

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(new byte[25 * 1024 * 1024 + 1], "movie.mp4", null, "video/mp4"));

        cut.WaitForAssertion(() => Assert.Contains("The media file is too large.", cut.Markup, StringComparison.Ordinal));
        Assert.Null(selected);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post && request.Path == "/api/admin/media");
    }

    [Fact]
    public async Task Editor_upload_rejection_shows_a_localized_error_without_selecting_an_asset()
    {
        var handler = new Handler(
            Response(HttpStatusCode.OK, SessionJson("Editor")),
            Response(HttpStatusCode.OK, "{\"token\":\"csrf\"}"),
            Response(HttpStatusCode.BadRequest, "{\"error\":\"unsupported_media\"}"));
        var state = await StateAsync(handler);
        Guid? selected = null;
        var cut = Render<MediaChooser>(parameters => parameters
            .AddCascadingValue(state)
            .Add(component => component.ValueChanged, EventCallback.Factory.Create<Guid?>(this, value => selected = value)));

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(PngBytes(), "photo.png", null, "image/png"));

        cut.WaitForAssertion(() => Assert.Contains("That media type is not supported.", cut.Markup, StringComparison.Ordinal));
        Assert.Null(selected);
        Assert.Empty(cut.FindAll("[data-testid='selected-media']"));
    }

    [Fact]
    public async Task Editor_url_fetch_shows_a_localized_error_without_selecting_an_asset()
    {
        var handler = new Handler(
            Response(HttpStatusCode.OK, SessionJson("Editor")),
            Response(HttpStatusCode.OK, """{"token":"csrf"}"""),
            Response(HttpStatusCode.BadRequest, """{"error":"unsupported_media"}"""));
        var state = await StateAsync(handler);
        Guid? selected = null;
        var cut = Render<MediaChooser>(parameters => parameters
            .AddCascadingValue(state)
            .Add(component => component.ValueChanged, EventCallback.Factory.Create<Guid?>(this, value => selected = value)));

        cut.Find("#media-url").Change("https://media.example/unsupported.gif");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("That media type is not supported.", cut.Markup, StringComparison.Ordinal));

        Assert.Null(selected);
        Assert.Empty(cut.FindAll("[data-testid='selected-media']"));
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.Path == "/api/admin/media/fetch");
    }

    [Fact]
    public async Task Editor_lookup_reloads_a_selected_asset_by_id()
    {
        var id = Guid.Parse("7c1ca179-9b7a-4acb-8697-2f46ef3921e4");
        var handler = new Handler(
            Response(HttpStatusCode.OK, SessionJson("Owner")),
            Response(HttpStatusCode.OK, AssetJson(id, kind: "audio")));
        var state = await StateAsync(handler);
        var cut = Render<MediaChooser>(parameters => parameters
            .AddCascadingValue(state)
            .Add(component => component.Value, id));

        cut.WaitForAssertion(() => Assert.Equal(id.ToString(), cut.Find("[data-testid='media-id']").TextContent));

        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Get && request.Path == $"/api/admin/media/{id}");
        Assert.Single(cut.FindAll("audio"));
    }

    [Fact]
    public async Task Moderator_has_no_media_editing_control()
    {
        var state = await StateAsync(Response(HttpStatusCode.OK, SessionJson("Moderator")));

        var cut = Render<MediaChooser>(parameters => parameters.AddCascadingValue(state));

        Assert.Empty(cut.FindAll("[data-testid='media-chooser']"));
    }

    [Fact]
    public async Task Signed_out_user_has_no_media_editing_control()
    {
        var state = await StateAsync(Response(HttpStatusCode.Unauthorized, """{"error":"unauthenticated"}"""));

        var cut = Render<MediaChooser>(parameters => parameters.AddCascadingValue(state));

        Assert.Empty(cut.FindAll("[data-testid='media-chooser']"));
    }

    private async Task<AdminState> StateAsync(params HttpResponseMessage[] responses) => await StateAsync(new Handler(responses));

    private static async Task<AdminState> StateAsync(Handler handler)
    {
        var state = new AdminState(new AdminApi(new ClientHttp(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://tenant.example/"),
        }, new NullReloader())));
        await state.InitializeAsync();
        return state;
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static string SessionJson(string role) => $$"""{"id":"21ac7193-52fd-4d4b-9194-5cd547448072","role":"{{role}}","isPlatform":false,"mustChangePassword":false}""";

    private static string AssetJson(Guid id, string kind = "image") => $$"""
        {"id":"{{id}}","hash":"hash","kind":"{{kind}}","mime":"{{(kind == "audio" ? "audio/mpeg" : "image/png")}}","bytes":4,"meta":{},"attribution":"Museum collection","url":"/media/hash"}
        """;

    private static byte[] PngBytes() => [137, 80, 78, 71, 13, 10, 26, 10];

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
        public void Register(ClientTranslation translation) => ArgumentNullException.ThrowIfNull(translation);
    }

    private sealed class MediaTranslator : IClientTranslator
    {
        private static readonly IReadOnlyDictionary<string, string> Values = new Dictionary<string, string>
        {
            ["admin.media.attribution"] = "Attribution",
            ["admin.media.upload"] = "Upload image, audio, or video",
            ["admin.media.url"] = "Media URL",
            ["admin.media.fetch"] = "Fetch media",
            ["admin.media.selected"] = "Selected asset ID",
            ["admin.media.media_too_large"] = "The media file is too large.",
            ["admin.media.unsupported_media"] = "That media type is not supported.",
            ["admin.media.error"] = "The media could not be saved.",
        };

        public string this[string key] => Values.TryGetValue(key, out var value) ? value : key;
    }
}
