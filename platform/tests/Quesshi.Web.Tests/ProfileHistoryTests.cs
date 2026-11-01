using System.Net;
using System.Text;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Shared;
using Quesshi.Web.Pages;
using Quesshi.Web.Plugins;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class ProfileHistoryTests : BunitContext
{
    private const string PlayerId = "21ac7193-52fd-4d4b-9194-5cd547448072";

    private static SessionHistoryEntry Entry(string name, string status, DateTimeOffset joined) =>
        new(Guid.Parse(name), "async_duel", status, joined, 0, null, null);

    private static Guid Id(int n) => Guid.Parse($"00000000-0000-4000-8000-{n:D12}");

    /// <summary>AC-6.</summary>
    [Fact]
    public void Completed_history_excludes_active_sessions_sorts_newest_first_and_limits_rows()
    {
        var origin = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var sessions = Enumerable.Range(1, 15)
            .Select(i => Entry(Id(i).ToString(), "finished", origin.AddDays(i)))
            .Append(Entry(Id(100).ToString(), "running", origin.AddDays(20)))
            .Append(Entry(Id(101).ToString(), "lobby", origin.AddDays(21)))
            .Append(Entry(Id(102).ToString(), "cancelled", origin.AddDays(22)));

        var result = ProfileHistory.Completed(sessions);

        Assert.Equal(12, result.Count);
        Assert.Equal(Id(15), result[0].Id);
        Assert.Equal(Id(4), result[^1].Id);
        Assert.All(result, session => Assert.Equal("finished", session.Status));
    }

    /// <summary>AC-7.</summary>
    [Fact]
    public async Task A_finished_session_on_the_profile_opens_the_platform_playback_route()
    {
        var finished = Id(7);
        var handler = new Handler($$"""
            {"sessions":[
              {"id":"{{Id(8)}}","format":"live","status":"running","joinedAt":"2026-03-02T10:00:00+00:00","score":0,"place":null,"outcome":null},
              {"id":"{{finished}}","format":"async_duel","status":"finished","joinedAt":"2026-03-01T10:00:00+00:00","score":4,"place":1,"outcome":"win"}],
             "next":null}
            """);
        var http = new ClientHttp(new HttpClient(handler) { BaseAddress = new Uri("https://tenant.example/") }, new NullReloader());
        JSInterop.Setup<string?>("quesshi.get", _ => true).SetResult(null);
        var translator = new Translator(http);
        await translator.UseAsync("en");
        Services.AddSingleton(translator);
        Services.AddSingleton<IClientTranslator>(translator);
        Services.AddSingleton(http);
        var auth = new AuthState(new PlayerApi(http));
        await auth.InitializeAsync();
        Services.AddSingleton(auth);
        Services.AddSingleton<IFormatClientRegistry>(new ClientRegistry(new NoLoader()));

        var cut = Render<Profile>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-test='profile-history'] [data-session-id]")));
        var row = cut.Find("[data-test='profile-history'] [data-session-id]");
        Assert.Equal(finished.ToString(), row.GetAttribute("data-session-id"));
        Assert.Equal($"/sessions/{finished}", row.QuerySelector("a")!.GetAttribute("href"));
        Assert.Equal($"/sessions/{finished}", ProfileHistory.Route(Entry(finished.ToString(), "finished", DateTimeOffset.UtcNow)));
    }

    private sealed class NoLoader : IClientAssemblyLoader
    {
        public Task<IReadOnlyList<System.Reflection.Assembly>> LoadAsync(string assemblyName) => throw new NotSupportedException();
    }

    private sealed class NullReloader : IClientReloader
    {
        public Task ReloadOnceAsync(string buildId) => Task.CompletedTask;
    }

    private sealed class Handler(string sessions) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/me") return Task.FromResult(Json($$"""{"id":"{{PlayerId}}","displayName":"North player","avatarSeed":"avatar-7","lang":"en"}"""));
            if (path.StartsWith("/i18n/", StringComparison.Ordinal)) return Task.FromResult(Json("{}"));
            if (path == "/api/me/sessions") return Task.FromResult(Json(sessions));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
