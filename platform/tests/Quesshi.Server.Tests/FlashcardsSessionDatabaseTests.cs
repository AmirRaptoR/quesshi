using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Engine.Contracts;
using Quesshi.Features.Leaderboard;
using Quesshi.Features.Stats;
using Quesshi.Formats.Testing;
using Quesshi.Kinds.Choice;
using Quesshi.Kinds.MapPick;
using Quesshi.Kinds.Order;
using Quesshi.Server.Sessions;
using Quesshi.Shared;
using Quesshi.Testing;

namespace Quesshi.Server.Tests;

/// <summary>A flashcards deck played through the real session, command and stream endpoints and the deadline sweep against PostgreSQL.</summary>
public sealed class FlashcardsSessionDatabaseTests(FlashcardsSessionDatabaseTests.FlashcardsHost host) : IClassFixture<FlashcardsSessionDatabaseTests.FlashcardsHost>
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // Each test starts at the host's start time, because the clients' tokens were issued then and a day-old token is refused.
    private readonly bool _clockReset = host.ResetClock();
    private HttpClient? _player;

    private HttpClient Player => _player ??= host.Fixture.Player;

    [SkippableFact]
    public async Task Creating_a_flashcards_session_from_a_pack_returns_one_already_running_at_slot_zero_on_the_front()
    {
        Skip.If(host.Fixture is null, "PostgreSQL is not configured.");
        var id = Guid.NewGuid();

        await StartDeckAsync(id, 3);

        var row = (await host.Fixture.QueryOwnerAsync<SessionRow>(
            "select status as Status, started_at is not null as Started, capacity as Capacity from sessions where id = @id", new { id })).Single();
        Assert.Equal(("running", true, 1), (row.Status, row.Started, row.Capacity));
        var view = await ViewAsync(id);
        Assert.Equal("running", Property(view, "Status").GetString());
        var body = Property(view, "Body");
        Assert.Equal((0, "front", 3), (Property(body, "Slot").GetInt32(), Property(body, "Side").GetString(), Property(body, "Total").GetInt32()));
    }

    [SkippableFact]
    public async Task A_retried_create_with_the_same_id_returns_the_same_session_and_no_second_one()
    {
        Skip.If(host.Fixture is null, "PostgreSQL is not configured.");
        var id = Guid.NewGuid();

        var first = await CreateAsync(id, count: 2);
        var firstBody = await first.Content.ReadAsStringAsync();
        var retry = await CreateAsync(id, count: 2);

        Assert.True(first.StatusCode == HttpStatusCode.Created, firstBody);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(
            JsonDocument.Parse(firstBody).RootElement.GetProperty("code").GetString(),
            JsonDocument.Parse(await retry.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        Assert.Equal(1, (await host.Fixture.QueryOwnerAsync<long>("select count(*) from sessions where id = @id", new { id })).Single());
        Assert.Equal(1, (await host.Fixture.QueryOwnerAsync<long>("select count(*) from session_participants where session_id = @id", new { id })).Single());
    }

    [SkippableFact]
    public async Task Before_a_flip_the_view_and_the_stream_payloads_hold_no_back_text_for_the_current_card()
    {
        Skip.If(host.Fixture is null, "PostgreSQL is not configured.");
        var id = Guid.NewGuid();
        await StartDeckAsync(id, 2);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var streamRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/stream?session={id}");
#if QUESSHI_TEST_IDENTITY
        streamRequest.Headers.Add("X-Quesshi-Test-Player", host.Fixture.PlayerId.ToString());
#endif
        using var stream = await Player.SendAsync(streamRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync(timeout.Token), Encoding.UTF8);

        var before = await ViewAsync(id);
        Assert.DoesNotContain("back-0", before.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await CommandAsync(id, "action", 0, 0, "flip", null)).StatusCode);
        var flipped = await ViewAsync(id);
        Assert.Contains("back-0", flipped.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await CommandAsync(id, "answer", 1, 0, null, "{\"rating\":2}")).StatusCode);

        var frames = new List<string>();
        try
        {
            while (!frames.Any(frame => frame.Contains("front-1", StringComparison.Ordinal)))
                frames.Add(await ReadFrameAsync(reader, timeout.Token));
        }
        catch (OperationCanceledException) { throw new Xunit.Sdk.XunitException("no frame for the next card arrived; frames: " + string.Join(" || ", frames)); }

        Assert.All(frames, frame => Assert.DoesNotContain("back-1", frame, StringComparison.Ordinal));
        Assert.DoesNotContain("back-1", (await ViewAsync(id)).GetRawText(), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Reloading_mid_deck_days_later_returns_the_same_card_and_side()
    {
        Skip.If(host.Fixture is null, "PostgreSQL is not configured.");
        var id = Guid.NewGuid();
        await StartDeckAsync(id, 3);
        await CommandAsync(id, "action", 0, 0, "flip", null);
        await CommandAsync(id, "answer", 1, 0, null, "{\"rating\":3}");
        await CommandAsync(id, "action", 0, 1, "flip", null);
        var before = Property(await ViewAsync(id), "Body").GetRawText();

        await AdvanceAsync(TimeSpan.FromDays(9));
        var after = Property(await ViewAsync(id), "Body").GetRawText();

        Assert.Equal(before, after);
        Assert.Equal("back", Property(JsonDocument.Parse(after).RootElement, "side").GetString());
    }

    [SkippableFact]
    public async Task At_the_deadline_the_sweep_completes_an_untouched_session_with_zero_ratings_and_no_error()
    {
        Skip.If(host.Fixture is null, "PostgreSQL is not configured.");
        var id = Guid.NewGuid();
        await StartDeckAsync(id, 2);

        await AdvanceAsync(TimeSpan.FromDays(30) + TimeSpan.FromMinutes(1));
        await SweepAsync();

        Assert.Equal(("finished", "completed"), (await StatusAsync(id), await EndReasonAsync(id)));
        Assert.Equal(new[] { 0, 0, 0, 0 }, await ResultAsync(id));
        Assert.Equal(0, (await host.Fixture.QueryOwnerAsync<long>("select count(*) from session_answers where session_id = @id", new { id })).Single());
    }

    [SkippableFact]
    public async Task At_the_deadline_a_partly_rated_session_counts_its_ratings_and_a_flip_on_day_29_keeps_it_running_until_day_59()
    {
        Skip.If(host.Fixture is null, "PostgreSQL is not configured.");
        var id = Guid.NewGuid();
        await StartDeckAsync(id, 4);
        foreach (var (slot, rating) in new[] { (0, 1), (1, 1), (2, 3) })
        {
            Assert.Equal(HttpStatusCode.OK, (await CommandAsync(id, "action", 0, slot, "flip", null)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await CommandAsync(id, "answer", 1, slot, null, $"{{\"rating\":{rating}}}")).StatusCode);
        }

        await AdvanceAsync(TimeSpan.FromDays(29));
        Assert.Equal(HttpStatusCode.OK, (await CommandAsync(id, "action", 0, 3, "flip", null)).StatusCode);
        await AdvanceAsync(TimeSpan.FromDays(29));
        await SweepAsync();
        Assert.Equal("running", await StatusAsync(id));

        await AdvanceAsync(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        await SweepAsync();

        Assert.Equal(("finished", "completed"), (await StatusAsync(id), await EndReasonAsync(id)));
        Assert.Equal(new[] { 0, 2, 0, 1 }, await ResultAsync(id));
    }

    [SkippableFact]
    public async Task A_finished_flashcards_session_adds_no_stats_row_and_no_leaderboard_entry()
    {
        Skip.If(host.Fixture is null, "PostgreSQL is not configured.");
        var id = Guid.NewGuid();
        await StartDeckAsync(id, 1);
        Assert.Equal(HttpStatusCode.OK, (await CommandAsync(id, "action", 0, 0, "flip", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CommandAsync(id, "answer", 1, 0, null, "{\"rating\":3}")).StatusCode);
        Assert.Equal(("finished", "completed"), (await StatusAsync(id), await EndReasonAsync(id)));

        foreach (var table in new[] { "player_results", "leaderboard_entries", "leaderboard_abandonments" })
            Assert.Equal(0, (await host.Fixture.QueryOwnerAsync<long>(
                $"select count(*) from {table} where player_id = @player", new { player = host.Fixture.PlayerId })).Single());
        Assert.Equal(0, (await host.Fixture.QueryOwnerAsync<long>(
            "select count(*) from player_stats where player_id = @player", new { player = host.Fixture.PlayerId })).Single());
    }

    /// <summary>Moves the clock and signs the player in again, because the antiforgery token the host issued is only good for a day.</summary>
    private async Task AdvanceAsync(TimeSpan duration)
    {
        host.Clock.Advance(duration);
        _player = await host.Fixture.CreateFreshPlayerClientAsync();
    }

    private async Task StartDeckAsync(Guid id, int count)
    {
        var created = await CreateAsync(id, count);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
    }

    private sealed record SessionRow(string Status, bool Started, int Capacity);

    private Task<HttpResponseMessage> CreateAsync(Guid id, int count) => Player.PostAsJsonAsync("/api/sessions", new
    {
        id,
        format = "flashcards",
        capacity = 1,
        rules = new { },
        content = new { source = "pack", parameters = new { packId = host.Pack }, count },
    });

    private async Task<JsonElement> ViewAsync(Guid id)
    {
        var response = await Player.GetAsync($"/api/sessions/{id}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<HttpResponseMessage> CommandAsync(Guid id, string type, int phase, int slot, string? name, string? payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{id}/commands")
        {
            Content = JsonContent.Create(new
            {
                type,
                name,
                at = new { phase = phase.ToString(), slot, step = 0 },
                revision = 0,
                payload = payload is null ? (JsonElement?)null : JsonDocument.Parse(payload).RootElement.Clone(),
            }, options: Web),
        };
#if QUESSHI_TEST_IDENTITY
        request.Headers.Add("X-Quesshi-Test-Player", host.Fixture.PlayerId.ToString());
#endif
        return await Player.SendAsync(request);
    }

    private async Task SweepAsync()
    {
        await host.Fixture.Services.GetRequiredService<DeadlineSweep>().RunPassAsync();
        // Deliveries are not awaited by the pass; give the grain a moment to commit.
        await Task.Delay(500);
    }

    private static JsonElement Property(JsonElement element, string name) =>
        element.EnumerateObject().Single(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

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

    private async Task<int[]> ResultAsync(Guid id)
    {
        var json = (await host.Fixture.QueryOwnerAsync<string>("select result::text from sessions where id = @id", new { id })).Single();
        return JsonDocument.Parse(json).RootElement.GetProperty("counts").EnumerateArray().Select(count => count.GetInt32()).ToArray();
    }

    public sealed class FlashcardsHost : IAsyncLifetime
    {
        public PlatformHttpFixture Fixture { get; private set; } = null!;
        public TestClock Clock { get; } = new(DateTimeOffset.UtcNow);
        private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

        public bool ResetClock()
        {
            Clock.Set(_startedAt);
            return true;
        }
        public Guid Pack { get; } = Guid.NewGuid();

        public async Task InitializeAsync()
        {
            if (Environment.GetEnvironmentVariable("QUESSHI_TEST_POSTGRES") is null) return;
            Fixture = new PlatformHttpFixture([new StatsFeature(), new LeaderboardFeature()])
            {
                ConfigureServices = services =>
                {
                    services.AddSingleton<IClock>(Clock);
                    services.AddSingleton<IKindRegistry>(new KindRegistry([new ChoiceKind(), new OrderKind(), new MapPickKind(), new TestFlashcardKind()]));
                },
            };
            await Fixture.StartAsync();
            await Fixture.SetCapabilityAsync("format", "flashcards", true);
            await Fixture.SetCapabilityAsync("kind", "flashcard", true);
            await Fixture.SetCapabilityAsync("source", "pack", true);
            await Fixture.ExecuteOwnerAsync("""
                insert into packs(tenant_id, id, owner_id, title, lang, visibility) values (@t, @pack, @player, 'Deck', 'en', 'private');
                insert into questions(tenant_id, id, kind, kind_version, family, lang, level, prompt, payload, status, source, owner_id)
                select @t, ('00000000-0000-0000-0000-0000000000' || lpad(n::text, 2, '0'))::uuid, 'flashcard', 1, 'card', 'en', null, 'front-' || n,
                       jsonb_build_object('front', 'front-' || n, 'back', 'back-' || n), 'approved', 'player', @player
                from generate_series(0, 5) as n;
                insert into pack_items(tenant_id, pack_id, question_id, position)
                select @t, @pack, ('00000000-0000-0000-0000-0000000000' || lpad(n::text, 2, '0'))::uuid, n from generate_series(0, 5) as n
                """, new { t = Fixture.TenantId, pack = Pack, player = Fixture.PlayerId });
        }

        public async Task DisposeAsync()
        {
            if (Fixture is not null) await Fixture.DisposeAsync();
        }
    }
}
