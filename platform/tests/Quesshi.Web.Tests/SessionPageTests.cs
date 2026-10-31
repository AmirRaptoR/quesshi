using System.Net;
using System.Text;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Shared;
using Quesshi.Web.Pages;
using Quesshi.Web.Plugins;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class SessionPageTests : BunitContext
{
    private readonly PlatformState state;

    public SessionPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new ClientHttp(new HttpClient(new Handler()) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader());
        state = new PlatformState(new AppConfig(http, null!));
        var registry = new ClientRegistry(new NoLoader());
        registry.RegisterFormat(new TestFormat());
        registry.RegisterSlot(ClientSlots.SessionResult, typeof(ResultContribution));
        registry.RegisterSlot(ClientSlots.SessionResult, typeof(BrokenContribution));
        Services.AddSingleton(new Translator(http));
        Services.AddSingleton(new LobbyActions(new PlayerApi(http)));
        Services.AddSingleton<IFormatClientRegistry>(registry);
        Services.AddSingleton(state);
    }

    [Fact]
    public void A_lobby_view_renders_the_shared_lobby_panel()
    {
        state.AcceptView(LobbyViews.Parse("amir", ["amir", "sara"], status: "lobby", format: "test-format"));

        var cut = Render<Session>(p => p.Add(c => c.Id, LobbyViews.SessionId));

        Assert.Single(cut.FindAll($"[data-lobby-panel='{LobbyViews.SessionId}']"));
    }

    [Fact]
    public void A_running_view_renders_the_screen_of_the_registered_format()
    {
        state.AcceptView(LobbyViews.Parse("amir", ["amir", "sara"], status: "running", format: "test-format"));

        var cut = Render<Session>(p => p.Add(c => c.Id, LobbyViews.SessionId));

        Assert.Equal("screen:test-format", cut.Find("[data-test-screen]").TextContent);
        Assert.Empty(cut.FindAll("[data-lobby-panel]"));
    }

    [Fact]
    public void A_finished_view_renders_the_result_of_the_registered_format()
    {
        state.AcceptView(LobbyViews.Parse("amir", ["amir", "sara"], status: "finished", format: "test-format"));

        var cut = Render<Session>(p => p.Add(c => c.Id, LobbyViews.SessionId));

        Assert.Equal("result:test-format", cut.Find("[data-test-result]").TextContent);
        Assert.Empty(cut.FindAll("[data-test-screen]"));
    }

    [Fact]
    public void A_cancelled_view_renders_the_generic_cancelled_screen()
    {
        state.AcceptView(LobbyViews.Parse("amir", ["amir", "sara"], status: "cancelled", format: "test-format"));

        var cut = Render<Session>(p => p.Add(c => c.Id, LobbyViews.SessionId));

        Assert.Single(cut.FindAll("[data-session-cancelled]"));
        Assert.Empty(cut.FindAll("[data-test-screen], [data-test-result], [data-lobby-panel]"));
    }

    [Fact]
    public void A_finished_view_shows_session_result_contributions_after_the_format_result_and_survives_a_broken_one()
    {
        state.AcceptView(LobbyViews.Parse("amir", ["amir", "sara"], status: "finished", format: "test-format"));

        var cut = Render<Session>(p => p.Add(c => c.Id, LobbyViews.SessionId));

        var markup = cut.Markup;
        Assert.True(markup.IndexOf("data-test-result", StringComparison.Ordinal) < markup.IndexOf("data-contribution", StringComparison.Ordinal));
        Assert.Equal("contribution:test-format", cut.Find("[data-contribution]").TextContent);
        Assert.Single(cut.FindAll("[data-slot-gap='session.result']"));
    }

    [Fact]
    public void A_newer_accepted_view_replaces_the_rendered_phase()
    {
        state.AcceptView(LobbyViews.Parse("amir", ["amir", "sara"], status: "lobby", format: "test-format", version: 1));
        var cut = Render<Session>(p => p.Add(c => c.Id, LobbyViews.SessionId));

        cut.InvokeAsync(() => state.AcceptView(LobbyViews.Parse("amir", ["amir", "sara"], status: "running", format: "test-format", version: 2)));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-test-screen]")));
    }

    [Fact]
    public void A_view_of_another_session_is_not_rendered()
    {
        state.AcceptView(LobbyViews.Parse("amir", ["amir", "sara"], status: "running", format: "test-format"));

        var cut = Render<Session>(p => p.Add(c => c.Id, "11111111-1111-1111-1111-111111111111"));

        Assert.Single(cut.FindAll("[data-session-loading]"));
        Assert.Empty(cut.FindAll("[data-test-screen]"));
    }

    [Fact]
    public void The_session_page_never_branches_on_a_specific_format_id()
    {
        var page = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PlayerPages", "Session.razor"));

        Assert.DoesNotMatch(@"\.Format\s*(==|!=|is\b)|""[a-z0-9-]+""\s*(==|!=)\s*\w*\.?Format|switch\s*\(\s*\w+\.Format|case\s+""", page);
        Assert.Contains("Formats.ResolveFormat(", page);
    }

    private sealed class TestFormat : IFormatClient
    {
        public string Id => "test-format";
        public Type Settings => typeof(TestScreen);
        public Type Screen => typeof(TestScreen);
        public Type Result => typeof(TestResult);
    }

    private sealed class TestScreen : ComponentBase
    {
        [Parameter] public FormatScreenParameters Parameters { get; set; } = default!;
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder b)
        {
            b.OpenElement(0, "div");
            b.AddAttribute(1, "data-test-screen", "");
            b.AddContent(2, $"screen:{Parameters.Session.Format}");
            b.CloseElement();
        }
    }

    private sealed class TestResult : ComponentBase
    {
        [Parameter] public FormatScreenParameters Parameters { get; set; } = default!;
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder b)
        {
            b.OpenElement(0, "div");
            b.AddAttribute(1, "data-test-result", "");
            b.AddContent(2, $"result:{Parameters.Session.Format}");
            b.CloseElement();
        }
    }

    private sealed class ResultContribution : ComponentBase
    {
        [Parameter] public SessionResultParameters Parameters { get; set; } = default!;
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder b)
        {
            b.OpenElement(0, "div");
            b.AddAttribute(1, "data-contribution", "");
            b.AddContent(2, $"contribution:{Parameters.Session.Format}");
            b.CloseElement();
        }
    }

    private sealed class BrokenContribution : ComponentBase
    {
        [Parameter] public SessionResultParameters Parameters { get; set; } = default!;
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder b) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"token":"csrf"}""", Encoding.UTF8, "application/json") });
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
