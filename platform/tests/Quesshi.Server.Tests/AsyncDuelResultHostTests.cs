using Quesshi.Features.Features.Friends;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Shared;
using Quesshi.Testing;
using Quesshi.Web.Plugins;
using Quesshi.Web.Plugins.Formats.AsyncDuel;
using Quesshi.Web.Services;

namespace Quesshi.Server.Tests;

/// <summary>The async duel result screen against the real host: its history requests are answered by the real endpoint over persisted rows.</summary>
public sealed class AsyncDuelResultHostTests(PostgresFixture postgres) : IAsyncLifetime, IClassFixture<PostgresFixture>
{
    private PlatformHttpFixture? _host;

    public async Task InitializeAsync()
    {
        if (postgres.ConnectionString is null) return;
        _host = new PlatformHttpFixture([new FriendsFeature()]);
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task A_hundred_question_duel_loads_one_page_of_history_at_a_time_from_the_real_endpoint()
    {
        var sessionId = await SeedFinishedDuelAsync(Host.PlayerId, questions: 100);
        var requests = new List<string>();
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new ClientHttp(new HttpClient(new Bridge(Host.Player, requests)) { BaseAddress = new Uri("http://tenant.test/") }, new NoReloader());
        var translator = new Translator(http);
        context.Services.AddSingleton(http);
        context.Services.AddSingleton(translator);
        context.Services.AddSingleton<IClientTranslator>(translator);
        context.Services.AddSingleton<IClientViewer>(new Viewer(translator));
        var registry = new ClientRegistry(new NoLoader());
        new WebPluginsEntry().Register(registry);
        context.Services.AddSingleton<IClientRegistry>(registry);

        var cut = context.Render<AsyncDuelResult>(p => p.Add(c => c.Parameters, new FormatScreenParameters(View(sessionId, Host.PlayerId, 100))));

        cut.WaitForAssertion(() => Assert.Equal(10, cut.FindAll("[data-duel-question-result]").Count), TimeSpan.FromSeconds(15));
        var first = Assert.Single(requests);
        Assert.Contains($"/api/sessions/{sessionId}/history", first);
        Assert.DoesNotContain("after=", first);
        Assert.Equal(Enumerable.Range(0, 10).Select(i => $"Question {i}"), cut.FindAll("[data-field='prompt']").Select(p => p.TextContent));

        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-duel-more]").HasAttribute("disabled")));
        cut.Find("[data-duel-more]").Click();

        cut.WaitForAssertion(() => Assert.Equal(20, cut.FindAll("[data-duel-question-result]").Count), TimeSpan.FromSeconds(15));
        Assert.Equal(2, requests.Count);
        Assert.EndsWith("after=9", requests[1]);
        Assert.Equal(Enumerable.Range(0, 20).Select(i => $"Question {i}"), cut.FindAll("[data-field='prompt']").Select(p => p.TextContent));
    }

    private PlatformHttpFixture Host
    {
        get
        {
            Skip.If(postgres.ConnectionString is null, "PostgreSQL is not configured.");
            return _host ?? throw new InvalidOperationException("The platform HTTP host did not start.");
        }
    }

    private async Task<Guid> SeedFinishedDuelAsync(Guid playerId, int questions)
    {
        var sessionId = Guid.NewGuid();
        await Host.ExecuteOwnerAsync("""
            insert into sessions(tenant_id, id, code, format, owner_id, lang, status, capacity, content, started_at, ended_at)
            values (@tenantId, @sessionId, @code, 'async_duel', @playerId, 'en', 'finished', 2, '{}'::jsonb, now(), now());
            insert into session_participants(tenant_id, session_id, player_id, seat, joined_at)
            values (@tenantId, @sessionId, @playerId, 0, now());
            insert into session_items(tenant_id, session_id, slot, kind, kind_version, prompt, payload, card, reveal)
            select @tenantId, @sessionId, n, 'choice', 1, 'Question ' || n, '{}'::jsonb,
                   '{"options":["Tehran","Paris"]}'::jsonb, '{"correctIndex":0,"correctOption":"Tehran"}'::jsonb
            from generate_series(0, @last) as n;
            insert into session_answers(tenant_id, session_id, slot, player_id, answer, received_at)
            select @tenantId, @sessionId, n, @playerId, '{"index":0}'::jsonb, now() from generate_series(0, @last) as n;
            """, new { tenantId = Host.TenantId, sessionId, code = $"D{sessionId:N}"[..12], playerId, last = questions - 1 });
        return sessionId;
    }

    private static SessionView View(Guid sessionId, Guid playerId, int total)
    {
        var me = playerId.ToString();
        var view = new JsonObject
        {
            ["id"] = sessionId.ToString(), ["code"] = "DUEL", ["format"] = "async_duel", ["status"] = "finished",
            ["phase"] = "2", ["slot"] = total - 1, ["step"] = 0, ["version"] = 1, ["configVersion"] = 1,
            ["now"] = DateTimeOffset.UtcNow.ToString("O"), ["deadline"] = null,
            ["me"] = new JsonObject { ["id"] = me, ["seat"] = 0, ["role"] = "player", ["status"] = "done", ["score"] = 5, ["place"] = 1, ["outcome"] = "win" },
            ["roster"] = new JsonObject
            {
                ["total"] = 1,
                ["seats"] = new JsonArray(new JsonObject { ["id"] = me, ["name"] = "ME", ["seat"] = 0, ["role"] = "player", ["status"] = "done", ["score"] = 5, ["place"] = 1, ["outcome"] = "win", ["online"] = true }),
            },
            ["content"] = new JsonObject { ["count"] = total },
            ["rules"] = new JsonObject(),
            ["body"] = new JsonObject { ["You"] = new JsonObject { ["Player"] = me, ["Total"] = total, ["Answers"] = new JsonArray() }, ["Others"] = new JsonArray() },
        };
        return PlatformState.ParseView(view.ToJsonString())!;
    }

    /// <summary>Carries the web client's relative requests to the real host through a fixture client that already holds its identity, recording each history request.</summary>
    private sealed class Bridge(HttpClient inner, List<string> requests) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("/history", StringComparison.Ordinal)) requests.Add(request.RequestUri.PathAndQuery);
            var forwarded = new HttpRequestMessage(request.Method, new Uri(inner.BaseAddress!, request.RequestUri.PathAndQuery)) { Content = request.Content };
            foreach (var header in request.Headers) forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return inner.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
    }

    private sealed class Viewer(Translator translator) : IClientViewer
    {
        public string LanguageCode => translator.Lang;
        public string Direction => translator.Direction;
        public bool IsGuest => false;
        public string Num(object? value) => translator.Num(value);
    }

    private sealed class NoReloader : IClientReloader
    {
        public Task ReloadOnceAsync(string buildId) => Task.CompletedTask;
    }

    private sealed class NoLoader : IClientAssemblyLoader
    {
        public Task<IReadOnlyList<System.Reflection.Assembly>> LoadAsync(string assemblyName) => throw new NotSupportedException();
    }
}
