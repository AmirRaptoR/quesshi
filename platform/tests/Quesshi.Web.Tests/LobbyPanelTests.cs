using System.Net;
using System.Text;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Shared;
using Quesshi.Web.Components;
using Quesshi.Web.Plugins;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class LobbyPanelTests : BunitContext
{
    private readonly Recorder recorder = new();

    public LobbyPanelTests()
    {
        var http = new ClientHttp(new HttpClient(recorder) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader());
        var registry = new ClientRegistry(new NoLoader());
        registry.RegisterFormat(new FakeFormat("fake", typeof(FakeSettings)));
        registry.RegisterFormat(new FakeFormat("broken", typeof(BrokenSettings)));
        Services.AddSingleton(new Translator(http));
        Services.AddSingleton(new LobbyActions(new PlayerApi(http)));
        Services.AddSingleton<IFormatClientRegistry>(registry);
    }

    [Fact]
    public void A_lobby_renders_the_settings_component_resolved_for_its_registered_format()
    {
        var view = LobbyViews.Parse("amir", ["amir", "sara"], format: "fake");

        var cut = Render<LobbyPanel>(p => p.Add(c => c.SessionView, view));

        Assert.Equal("settings:fake:owner", cut.Find("[data-lobby-settings] [data-fake-settings]").TextContent);
    }

    [Fact]
    public void A_non_owner_gets_the_settings_component_without_edit_rights()
    {
        var view = LobbyViews.Parse("sara", ["amir", "sara"], format: "fake");

        var cut = Render<LobbyPanel>(p => p.Add(c => c.SessionView, view));

        Assert.Equal("settings:fake:guest", cut.Find("[data-fake-settings]").TextContent);
    }

    [Fact]
    public void A_throwing_settings_component_leaves_a_gap_and_the_rest_of_the_lobby_usable()
    {
        var view = LobbyViews.Parse("amir", ["amir", "sara"], format: "broken");

        var cut = Render<LobbyPanel>(p => p.Add(c => c.SessionView, view));

        Assert.Single(cut.FindAll("[data-settings-gap='broken']"));
        Assert.Equal(2, cut.FindAll("[data-seat]").Count);
    }

    [Fact]
    public void The_roster_marks_each_seat_online_or_offline_from_the_view()
    {
        var view = LobbyViews.Parse("amir", ["amir", "sara"], online: [true, false]);

        var cut = Render<LobbyPanel>(p => p.Add(c => c.SessionView, view));

        Assert.Equal("true", cut.Find("[data-seat='amir']").GetAttribute("data-online"));
        Assert.Equal("false", cut.Find("[data-seat='sara']").GetAttribute("data-online"));
        Assert.Equal("lobby.offline", cut.Find("[data-seat='sara'] [data-presence]").TextContent);
    }

    [Fact]
    public void A_newer_view_updates_the_roster_in_place()
    {
        var first = LobbyViews.Parse("amir", ["amir"], online: [true]);
        var cut = Render<LobbyPanel>(p => p.Add(c => c.SessionView, first));

        cut.Render(p => p.Add(c => c.SessionView, LobbyViews.Parse("amir", ["amir", "sara"], online: [true, true], version: 2)));

        Assert.Equal("true", cut.Find("[data-seat='sara']").GetAttribute("data-online"));
    }

    [Fact]
    public void The_share_action_hands_out_a_link_containing_the_join_code()
    {
        JSInterop.Setup<bool>("quesshi.share", _ => true).SetResult(true);
        var view = LobbyViews.Parse("amir", ["amir"], code: "ZK7Q4M");
        var cut = Render<LobbyPanel>(p => p.Add(c => c.SessionView, view));

        cut.Find("[data-lobby-share]").Click();

        var shared = Assert.Single(JSInterop.Invocations, call => call.Identifier == "quesshi.share");
        Assert.Contains("/join/ZK7Q4M", shared.Arguments[0]!.ToString());
    }

    [Fact]
    public void The_share_action_copies_the_link_when_the_browser_has_no_share_sheet()
    {
        JSInterop.Setup<bool>("quesshi.share", _ => true).SetResult(false);
        JSInterop.Setup<bool>("quesshi.copy", _ => true).SetResult(true);
        var view = LobbyViews.Parse("amir", ["amir"], code: "ZK7Q4M");
        var cut = Render<LobbyPanel>(p => p.Add(c => c.SessionView, view));

        cut.Find("[data-lobby-share]").Click();

        var copied = Assert.Single(JSInterop.Invocations, call => call.Identifier == "quesshi.copy");
        Assert.EndsWith("/join/ZK7Q4M", copied.Arguments[0]!.ToString());
    }

    [Fact]
    public void Only_the_owner_with_company_can_press_start_and_it_posts_to_the_start_endpoint()
    {
        var alone = Render<LobbyPanel>(p => p.Add(c => c.SessionView, LobbyViews.Parse("amir", ["amir"])));
        var guest = Render<LobbyPanel>(p => p.Add(c => c.SessionView, LobbyViews.Parse("sara", ["amir", "sara"])));
        var owner = Render<LobbyPanel>(p => p.Add(c => c.SessionView, LobbyViews.Parse("amir", ["amir", "sara"])));

        Assert.True(alone.Find("[data-lobby-start]").HasAttribute("disabled"));
        Assert.Empty(guest.FindAll("[data-lobby-start]"));
        owner.Find("[data-lobby-start]").Click();

        Assert.Equal($"POST /api/sessions/{LobbyViews.SessionId}/start", Assert.Single(recorder.Mutations));
    }

    [Fact]
    public void Leaving_posts_to_the_leave_endpoint()
    {
        var cut = Render<LobbyPanel>(p => p.Add(c => c.SessionView, LobbyViews.Parse("sara", ["amir", "sara"])));

        cut.Find("[data-lobby-leave]").Click();

        Assert.Equal($"POST /api/sessions/{LobbyViews.SessionId}/leave", Assert.Single(recorder.Mutations));
    }

    [Fact]
    public void Inviting_posts_the_invitee_once_and_then_reads_invited()
    {
        var invitee = new LobbyInvitee("3f0f6d3c-7c4b-4a0a-9a52-1d1f6a3b0099", "Cam");
        var cut = Render<LobbyPanel>(p => p
            .Add(c => c.SessionView, LobbyViews.Parse("amir", ["amir"]))
            .Add(c => c.Invitees, [invitee]));

        cut.Find($"[data-invitee='{invitee.Id}']").Click();

        Assert.Equal($"POST /api/sessions/{LobbyViews.SessionId}/invites", Assert.Single(recorder.Mutations));
        Assert.Contains(invitee.Id, recorder.Bodies.Single());
        Assert.True(cut.Find($"[data-invitee='{invitee.Id}']").HasAttribute("disabled"));
        Assert.Contains("lobby.invite.invited", cut.Find($"[data-invitee='{invitee.Id}']").TextContent);
    }

    [Fact]
    public void A_refused_action_shows_an_error_and_keeps_the_panel()
    {
        recorder.Refuse = true;
        var cut = Render<LobbyPanel>(p => p.Add(c => c.SessionView, LobbyViews.Parse("sara", ["amir", "sara"])));

        cut.Find("[data-lobby-leave]").Click();

        Assert.Single(cut.FindAll("[role='alert']"));
        Assert.Equal(2, cut.FindAll("[data-seat]").Count);
    }

    [Fact]
    public void A_session_that_left_the_lobby_shows_it_has_started()
    {
        var cut = Render<LobbyPanel>(p => p.Add(c => c.SessionView, LobbyViews.Parse("amir", ["amir", "sara"], status: "running")));

        Assert.Empty(cut.FindAll("[data-lobby-panel]"));
        Assert.Contains("lobby.alreadyStarted", cut.Markup);
    }

    private sealed class Recorder : HttpMessageHandler
    {
        public List<string> Mutations { get; } = [];
        public List<string> Bodies { get; } = [];
        public bool Refuse { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get)
                return path == "/api/antiforgery" ? Json(HttpStatusCode.OK, """{"token":"csrf"}""") : new HttpResponseMessage(HttpStatusCode.NotFound);
            Mutations.Add($"{request.Method} {path}");
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return Refuse ? Json(HttpStatusCode.Conflict, """{"error":"not_in_lobby"}""") : Json(HttpStatusCode.OK, """{"code":"ok"}""");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
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

    private sealed class FakeFormat(string id, Type settings) : IFormatClient
    {
        public string Id => id;
        public Type Settings => settings;
        public Type Screen => settings;
        public Type Result => settings;
    }

    private sealed class BrokenSettings : ComponentBase
    {
        [Parameter] public FormatSettingsParameters Parameters { get; set; } = default!;
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder b) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class FakeSettings : ComponentBase
    {
        [Parameter] public FormatSettingsParameters Parameters { get; set; } = default!;
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder b)
        {
            b.OpenElement(0, "span");
            b.AddAttribute(1, "data-fake-settings", "");
            b.AddContent(2, $"settings:{Parameters.Session.Format}:{(Parameters.IsOwner ? "owner" : "guest")}");
            b.CloseElement();
        }
    }
}
