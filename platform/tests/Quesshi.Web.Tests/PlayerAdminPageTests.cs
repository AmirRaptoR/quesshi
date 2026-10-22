using System.Net;
using System.Text;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Web.Admin;
using Quesshi.Web.Plugins;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class PlayerAdminPageTests : BunitContext
{
    public PlayerAdminPageTests() => Services.AddSingleton<IClientTranslator>(new Translator());

    [Fact]
    public async Task Moderator_player_screen_pages_searches_and_bans_through_the_player_route()
    {
        var target = Guid.Parse("eebd7be2-fdb1-4c8b-b363-805051401f4e");
        var handler = new Handler(target);
        var state = await StateAsync(handler);

        var cut = Render<AdminPlayers>(parameters => parameters.AddCascadingValue(state));
        cut.WaitForState(() => cut.FindAll("[data-testid=player-row]").Count == 25);
        cut.Find("[data-testid=player-next]").Click();
        cut.WaitForAssertion(() => Assert.Equal("2", cut.Find("[data-testid=player-page]").TextContent));

        cut.Find("[data-testid=player-search]").Change("Target player");
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Contains("Target player", cut.Markup, StringComparison.Ordinal));
        cut.Find("[data-testid=player-ban]").Click();
        cut.WaitForAssertion(() => Assert.Contains("admin.players.banned", cut.Markup, StringComparison.Ordinal));

        Assert.Contains("/api/admin/players?page=2&pageSize=25&q=", handler.Paths);
        Assert.Contains("/api/admin/players?page=1&pageSize=25&q=Target%20player", handler.Paths);
        Assert.Contains((HttpMethod.Post, $"/api/admin/players/{target}/ban"), handler.Requests);
    }

    [Fact]
    public async Task Editor_cannot_open_player_operation_controls()
    {
        var state = await StateAsync(new Handler(Guid.NewGuid(), "Editor"));

        var cut = Render<AdminPlayers>(parameters => parameters.AddCascadingValue(state));

        Assert.Empty(cut.FindAll("[data-testid=player-screen]"));
        Assert.Contains("admin.players.denied", cut.Markup, StringComparison.Ordinal);
    }

    private async Task<AdminState> StateAsync(Handler handler)
    {
        var state = new AdminState(new AdminApi(new ClientHttp(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://tenant.example/")
        }, new NullReloader())));
        await state.InitializeAsync();
        return state;
    }

    private sealed class Handler(Guid target, string role = "Moderator") : HttpMessageHandler
    {
        private int listRequests;
        public List<(HttpMethod Method, string Path)> Requests { get; } = [];
        public List<string> Paths => Requests.Select(request => request.Path).ToList();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            Requests.Add((request.Method, path));
            return Task.FromResult(path switch
            {
                "/api/admin/auth/me" => Response(HttpStatusCode.OK, SessionJson(role)),
                "/api/admin/antiforgery" => Response(HttpStatusCode.OK, "{\"token\":\"test-token\"}"),
                var value when request.Method == HttpMethod.Post && value == $"/api/admin/players/{target}/ban" => Response(HttpStatusCode.NoContent, string.Empty),
                var value when value.StartsWith("/api/admin/players?", StringComparison.Ordinal) => List(value),
                _ => throw new InvalidOperationException($"Unexpected request {request.Method} {path}.")
            });
        }

        private HttpResponseMessage List(string path)
        {
            listRequests++;
            if (path.Contains("q=Target%20player", StringComparison.Ordinal))
                return Response(HttpStatusCode.OK, PageJson(target, "Target player", listRequests > 3, 1, 1));
            if (path.Contains("page=2", StringComparison.Ordinal))
                return Response(HttpStatusCode.OK, PageJson(target, "Target player", false, 2, 1, 50));
            return Response(HttpStatusCode.OK, PageJson(Guid.NewGuid(), "Other player", false, 1, 25, 50));
        }
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static string SessionJson(string role) =>
        $$"""{"id":"21ac7193-52fd-4d4b-9194-5cd547448072","role":"{{role}}","isPlatform":false,"mustChangePassword":false}""";

    private static string PageJson(Guid id, string name, bool banned, int page, int count, int? total = null)
    {
        var items = Enumerable.Range(0, count)
            .Select(index => PlayerJson(index == 0 ? id : Guid.NewGuid(), index == 0 ? name : "Other player", index == 0 && banned));
        return $$"""{"items":[{{string.Join(',', items)}}],"total":{{total ?? count}},"page":{{page}},"pageSize":25}""";
    }

    private static string PlayerJson(Guid id, string name, bool banned) =>
        $$"""{"id":"{{id}}","displayName":"{{name}}","isBanned":{{banned.ToString().ToLowerInvariant()}}}""";

    private sealed class NullReloader : IClientReloader
    {
        public Task ReloadOnceAsync(string buildId) => Task.CompletedTask;
    }

    private sealed class Translator : IClientTranslator
    {
        public string this[string key] => key;
    }
}
