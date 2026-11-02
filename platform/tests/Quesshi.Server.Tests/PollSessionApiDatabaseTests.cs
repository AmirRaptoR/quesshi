using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Quesshi.Plugin.Api;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

/// <summary>A poll played through the real session, command and stream endpoints against PostgreSQL.</summary>
public sealed class PollSessionApiDatabaseTests(PollSessionApiDatabaseTests.PollHost host) : IClassFixture<PollSessionApiDatabaseTests.PollHost>
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [SkippableFact]
    public async Task Before_the_poll_finishes_another_players_choice_is_absent_from_the_view_and_stream_payloads()
    {
        Skip.If(host.Fixture is null, "PostgreSQL is not configured.");
        var game = await StartAsync(3, count: 2);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var streamRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/stream?session={game.Id}");
#if QUESSHI_TEST_IDENTITY
        streamRequest.Headers.Add("X-Quesshi-Test-Player", game.Players[0].ToString());
#endif
        using var stream = await game.Clients[0].SendAsync(streamRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync(timeout.Token), Encoding.UTF8);

        var second = await AnswerAsync(game, 1, "{\"option\":\"b\"}", revision: 1);
        var frames = new List<string>();
        try
        {
            while (!frames.Any(frame => frame.Contains("AnsweredSeats", StringComparison.OrdinalIgnoreCase) && frame.Contains(game.Players[1].ToString(), StringComparison.Ordinal)))
                frames.Add(await ReadFrameAsync(reader, timeout.Token));
        }
        catch (OperationCanceledException) { throw new Xunit.Sdk.XunitException("no answered view arrived; frames: " + string.Join(" || ", frames)); }
        var own = await ViewAsync(game, 0);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Contains("\"option\":\"b\"", (await second.Content.ReadAsStringAsync()).Replace(" ", ""), StringComparison.Ordinal);
        Assert.All(frames, frame => Assert.DoesNotContain("\"option\":\"b\"", frame.Replace(" ", ""), StringComparison.Ordinal));
        Assert.DoesNotContain("\"option\":\"b\"", own.GetRawText().Replace(" ", ""), StringComparison.Ordinal);
        Assert.Contains(game.Players[1].ToString(), BodyProperty(own, "AnsweredSeats").EnumerateArray().Select(seat => seat.GetString()));
        Assert.Equal(JsonValueKind.Null, BodyProperty(own, "Answer").ValueKind);
    }

    [SkippableFact]
    public async Task A_changed_answer_before_the_last_active_player_answers_replaces_the_stored_answer_without_a_second_one()
    {
        Skip.If(host.Fixture is null, "PostgreSQL is not configured.");
        var game = await StartAsync(3, count: 2);
        foreach (var player in game.Players.Index())
            Assert.Equal(HttpStatusCode.OK, (await AnswerAsync(game, player.Index, "{\"option\":\"a\"}", revision: 1)).StatusCode);
        Assert.Equal("running", await StatusAsync(game.Id));

        var last = new Game(game.Id, game.Players, game.Clients, Slot: 1);
        Assert.Equal(HttpStatusCode.OK, (await AnswerAsync(last, 0, "{\"option\":\"a\"}", revision: 1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AnswerAsync(last, 1, "{\"option\":\"a\"}", revision: 1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AnswerAsync(last, 0, "{\"option\":\"b\"}", revision: 2)).StatusCode);
        Assert.Equal("running", await StatusAsync(game.Id));
        Assert.Equal(HttpStatusCode.OK, (await AnswerAsync(last, 2, "{\"option\":\"a\"}", revision: 1)).StatusCode);

        var rows = await host.Fixture.QueryOwnerAsync<AnswerRow>(
            "select player_id as Player, revision as Revision, answer->>'option' as Choice from session_answers where session_id = @id and slot = 1 order by player_id",
            new { id = game.Id });
        Assert.Equal(3, rows.Count);
        var changed = Assert.Single(rows, row => row.Player == game.Players[0]);
        Assert.Equal(("b", 2), (changed.Choice, changed.Revision));
        Assert.All(rows.Where(row => row.Player != game.Players[0]), row => Assert.Equal(("a", 1), (row.Choice, row.Revision)));
        Assert.Equal(("finished", "completed"), (await StatusAsync(game.Id), await EndReasonAsync(game.Id)));
    }

    [SkippableFact]
    public async Task A_poll_ends_as_no_contest_when_too_few_active_players_remain()
    {
        Skip.If(host.Fixture is null, "PostgreSQL is not configured.");
        var game = await StartAsync(3, count: 2);
        Assert.Equal(HttpStatusCode.OK, (await AnswerAsync(game, 0, "{\"option\":\"a\"}", revision: 1)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await CommandAsync(game, 1, "leave", null, null)).StatusCode);
        Assert.Equal("running", await StatusAsync(game.Id));
        Assert.Equal(HttpStatusCode.OK, (await CommandAsync(game, 2, "leave", null, null)).StatusCode);

        Assert.Equal(("finished", "no_contest"), (await StatusAsync(game.Id), await EndReasonAsync(game.Id)));
    }

    private sealed record Game(Guid Id, IReadOnlyList<Guid> Players, IReadOnlyList<HttpClient> Clients, int Slot = 0);
    private sealed record AnswerRow(Guid Player, int Revision, string Choice);

    private async Task<Game> StartAsync(int players, int count)
    {
        var fixture = host.Fixture;
        var clients = new List<HttpClient> { fixture.Player };
        var ids = new List<Guid> { fixture.PlayerId };
        for (var index = 1; index < players; index++)
        {
            var (client, id) = await fixture.CreateSecondPlayerClientAsync();
            clients.Add(client);
            ids.Add(id);
        }

        var id0 = Guid.NewGuid();
        var created = await fixture.Player.PostAsJsonAsync("/api/sessions", new
        {
            id = id0, format = "poll", capacity = 8, rules = new { },
            content = new { source = "bank", parameters = new { categories = new[] { host.Category } }, count },
        });
        var body = await created.Content.ReadAsStringAsync();
        Assert.True(created.StatusCode == HttpStatusCode.Created, body);
        var code = JsonDocument.Parse(body).RootElement.GetProperty("code").GetString();
        foreach (var client in clients.Skip(1))
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/sessions/join", new { code })).StatusCode);
        var started = await fixture.Player.PostAsync($"/api/sessions/{id0}/start", null);
        Assert.True(started.StatusCode == HttpStatusCode.OK, await started.Content.ReadAsStringAsync());
        return new Game(id0, ids, clients);
    }

    private async Task<JsonElement> ViewAsync(Game game, int player)
    {
        var response = await game.Clients[player].GetAsync($"/api/sessions/{game.Id}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<HttpResponseMessage> AnswerAsync(Game game, int player, string answer, int revision) =>
        await CommandAsync(game, player, "answer", answer, revision);

    private async Task<HttpResponseMessage> CommandAsync(Game game, int player, string type, string? answer, int? revision)
    {
        var view = await ViewAsync(game, player);
        var phase = Property(view, "Phase").ToString();
        var slot = Property(view, "Slot").GetInt32();
        var step = Property(view, "Step").GetInt32();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{game.Id}/commands")
        {
            Content = JsonContent.Create(new
            {
                type, at = new { phase, slot, step }, revision, payload = answer is null ? (JsonElement?)null : JsonDocument.Parse(answer).RootElement.Clone(),
            }, options: Web),
        };
#if QUESSHI_TEST_IDENTITY
        request.Headers.Add("X-Quesshi-Test-Player", game.Players[player].ToString());
#endif
        return await game.Clients[player].SendAsync(request);
    }

    private static JsonElement Property(JsonElement element, string name) =>
        element.EnumerateObject().Single(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static JsonElement BodyProperty(JsonElement view, string name) => Property(Property(view, "Body"), name);

    private static async Task<string> ReadFrameAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        string? data = null;
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken) ?? throw new InvalidOperationException("The stream ended before a frame was received.");
            if (line.Length == 0 && data is not null) return data;
            if (line.StartsWith("data: ", StringComparison.Ordinal)) data = line[6..];
        }
    }

    private async Task<string> StatusAsync(Guid id) =>
        (await host.Fixture.QueryOwnerAsync<string>("select status from sessions where id = @id", new { id })).Single();

    private async Task<string?> EndReasonAsync(Guid id) =>
        (await host.Fixture.QueryOwnerAsync<string?>("select end_reason from sessions where id = @id", new { id })).Single();

    public sealed class PollHost : IAsyncLifetime
    {
        public PlatformHttpFixture Fixture { get; private set; } = null!;
        public Guid Category { get; } = Guid.NewGuid();

        public async Task InitializeAsync()
        {
            if (Environment.GetEnvironmentVariable("QUESSHI_TEST_POSTGRES") is null) return;
            Fixture = new PlatformHttpFixture([new PollFeature()]);
            await Fixture.StartAsync();
            await Fixture.SetCapabilityAsync("format", "poll", true);
            await Fixture.SetCapabilityAsync("kind", "poll_choice", true);
            await Fixture.ExecuteOwnerAsync("""
                insert into categories(tenant_id, id, slug, names) values (@t, @category, 'poll-api-category', '{"en":"Poll"}'::jsonb);
                insert into tenant_capabilities(tenant_id, type, key, entitled, enabled, settings)
                values (@t, 'source', 'bank', true, true, jsonb_build_object('CategoriesByFamily', jsonb_build_object('poll', jsonb_build_array(@category::text))))
                on conflict (tenant_id, type, key) do update set entitled = true, enabled = true, settings = excluded.settings;
                insert into questions(tenant_id, id, kind, kind_version, family, lang, category_id, level, prompt, payload, fingerprint, status, source)
                select @t, gen_random_uuid(), 'poll_choice', 1, 'poll', 'en', @category, 1, 'Poll prompt ' || n,
                       '{"options":[{"id":"a","text":"A"},{"id":"b","text":"B"}]}'::jsonb, 'poll-api-' || n, 'approved', 'admin'
                from generate_series(1, 60) as n
                """, new { t = Fixture.TenantId, category = Category });
        }

        public async Task DisposeAsync()
        {
            if (Fixture is not null) await Fixture.DisposeAsync();
        }
    }

    private sealed class PollFeature : IFeature
    {
        public string Id => "poll_session_api";
        public FeatureSettings Settings => FeatureSettings.Create(new PollSettings(), _ => []);
        public void Configure(IFeatureBuilder feature) { }
    }

    private sealed record PollSettings;
}
