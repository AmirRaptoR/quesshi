using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Engine.Contracts;
using Quesshi.Plugin.Api;
using Quesshi.Server.Streaming;
using Quesshi.Testing;
using Xunit.Sdk;

namespace Quesshi.Server.Tests;

/// <summary>Presence is refreshed by the hosted stream reconciliation loop and rendered by its real session view path.</summary>
public sealed class StreamPresenceDatabaseTests(PostgresFixture postgres) : IAsyncLifetime, IClassFixture<PostgresFixture>
{
    private readonly TestClock _clock = new(DateTimeOffset.Parse("2026-10-23T00:00:00Z"));
    private PlatformHttpFixture? _host;

    public async Task InitializeAsync()
    {
        if (postgres.ConnectionString is null) return;
        _host = new PlatformHttpFixture([new PresenceStreamFeature()])
        {
            ConfigureServices = services => services.AddSingleton<IClock>(_clock),
        };
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task Signed_in_stream_writes_last_seen_at_within_the_presence_interval()
    {
        await Host.ExecuteOwnerAsync("update players set last_seen_at = null where id = @playerId", new { playerId = Host.PlayerId });
        await using var stream = await OpenStreamAsync(Host.Player, Host.PlayerId);
        await WaitForAsync(() => Hub.SubscriptionCount == 1);

        await Hub.ReconcileAsync();

        var lastSeenAt = Assert.Single(await Host.QueryOwnerAsync<DateTimeOffset?>(
            "select last_seen_at from players where id = @playerId", new { playerId = Host.PlayerId }));
        Assert.Equal(_clock.Now, lastSeenAt);
    }

    [SkippableFact]
    public async Task Closed_last_stream_marks_a_player_offline_after_sixty_one_seconds_in_the_lobby_roster()
    {
        var (watcherClient, watcherId) = await Host.CreateSecondPlayerClientAsync();
        using var client = watcherClient;
        var sessionId = await CreateLobbyAsync(Host.PlayerId, watcherId);
        await using var watcher = await OpenStreamAsync(client, watcherId, sessionId);
        _ = await ReadFrameAsync(watcher.Reader);
        await using (var presence = await OpenStreamAsync(Host.Player, Host.PlayerId))
        {
            await Hub.ReconcileAsync();
            _ = await ReadFrameAsync(watcher.Reader);
        }
        await WaitForAsync(() => Hub.SubscriptionCount == 1);
        _clock.Advance(TimeSpan.FromSeconds(61));

        await Hub.ReconcileAsync();

        var refreshed = await ReadFrameAsync(watcher.Reader);
        Assert.False(IsOnline(refreshed, Host.PlayerId));
    }

    [SkippableFact]
    public async Task Closing_one_of_two_tabs_keeps_the_player_online_in_the_lobby_roster()
    {
        var sessionId = await CreateLobbyAsync(Host.PlayerId);
        await using var first = await OpenStreamAsync(Host.Player, Host.PlayerId, sessionId);
        using var secondClient = await Host.CreateFreshPlayerClientAsync();
        await using var second = await OpenStreamAsync(secondClient, Host.PlayerId, sessionId);
        _ = await ReadFrameAsync(first.Reader);
        _ = await ReadFrameAsync(second.Reader);
        await Hub.ReconcileAsync();
        _ = await ReadFrameAsync(first.Reader);
        _ = await ReadFrameAsync(second.Reader);

        await first.DisposeAsync();
        await WaitForAsync(() => Hub.SubscriptionCount == 1);
        _clock.Advance(PresenceTiming.RefreshInterval);
        await Hub.ReconcileAsync();

        var refreshed = await ReadFrameAsync(second.Reader);
        Assert.True(IsOnline(refreshed, Host.PlayerId));
    }

    [SkippableFact]
    public async Task Two_open_tabs_update_a_players_presence_once_per_interval()
    {
        await CreatePresenceUpdateLogAsync();
        await using var first = await OpenStreamAsync(Host.Player, Host.PlayerId);
        using var secondClient = await Host.CreateFreshPlayerClientAsync();
        await using var second = await OpenStreamAsync(secondClient, Host.PlayerId);
        await WaitForAsync(() => Hub.SubscriptionCount == 2);

        await Hub.ReconcileAsync();

        Assert.Equal(1, await PresenceUpdateStatementCountAsync());
    }

    [SkippableFact]
    public async Task Five_thousand_open_streams_issue_one_presence_update_statement_per_tenant_interval()
    {
        var playerIds = await CreatePlayersAsync(5_000);
        await CreatePresenceUpdateLogAsync();
        var subscriptions = playerIds.Select(playerId => Hub.Subscribe(Host.TenantId, playerId.ToString(), null, isGuest: false)).ToArray();
        try
        {
            await Hub.ReconcileAsync();

            Assert.Equal(1, await PresenceUpdateStatementCountAsync());
        }
        finally
        {
            foreach (var subscription in subscriptions) Hub.Unsubscribe(subscription);
        }
    }

    [SkippableFact]
    public async Task Lobby_presence_refresh_keeps_the_session_version_unchanged()
    {
        var (watcherClient, watcherId) = await Host.CreateSecondPlayerClientAsync();
        using var client = watcherClient;
        var sessionId = await CreateLobbyAsync(Host.PlayerId, watcherId);
        await using var watcher = await OpenStreamAsync(client, watcherId, sessionId);
        var initial = await ReadFrameAsync(watcher.Reader);
        await using var presence = await OpenStreamAsync(Host.Player, Host.PlayerId);
        await WaitForAsync(() => Hub.SubscriptionCount == 2);

        await Hub.ReconcileAsync();

        var refreshed = await ReadFrameAsync(watcher.Reader);
        Assert.Equal(Version(initial), Version(refreshed));
    }

    [SkippableFact]
    public async Task Lobby_presence_refresh_delivers_changed_online_marks_without_a_session_write()
    {
        var (watcherClient, watcherId) = await Host.CreateSecondPlayerClientAsync();
        using var client = watcherClient;
        var sessionId = await CreateLobbyAsync(Host.PlayerId, watcherId);
        await using var watcher = await OpenStreamAsync(client, watcherId, sessionId);
        _ = await ReadFrameAsync(watcher.Reader);
        await using var presence = await OpenStreamAsync(Host.Player, Host.PlayerId);
        await WaitForAsync(() => Hub.SubscriptionCount == 2);

        await Hub.ReconcileAsync();

        var refreshed = await ReadFrameAsync(watcher.Reader);
        Assert.True(IsOnline(refreshed, Host.PlayerId));
    }

    private PlatformHttpFixture Host
    {
        get
        {
            Skip.If(postgres.ConnectionString is null, "PostgreSQL is not configured.");
            return _host ?? throw new InvalidOperationException("The platform HTTP host did not start.");
        }
    }

    private StreamHub Hub => Host.Services.GetRequiredService<StreamHub>();

    private async Task<Guid> CreateLobbyAsync(params Guid[] players)
    {
        var sessionId = Guid.NewGuid();
        await Host.ExecuteOwnerAsync("""
            insert into sessions(tenant_id, id, code, format, owner_id, lang, status, capacity, content, version)
            values (@tenantId, @sessionId, @code, 'async_duel', @ownerId, 'en', 'lobby', @capacity, '{}'::jsonb, 1);
            insert into session_participants(tenant_id, session_id, player_id, seat, role, status, phase, slot, step)
            select @tenantId, @sessionId, player_id, seat - 1, 'player', 'active', 'idle', -1, 0
            from unnest(@playerIds) with ordinality as players(player_id, seat);
            """, new { tenantId = Host.TenantId, sessionId, ownerId = players[0], capacity = players.Length, playerIds = players, code = Code() });
        return sessionId;
    }

    private async Task<Guid[]> CreatePlayersAsync(int count)
    {
        var accountIds = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        var playerIds = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        await Host.ExecuteOwnerAsync("""
            insert into accounts(id, email)
            select id, 'presence-' || id::text || '@example.test'
            from unnest(@accountIds) as accounts(id);
            insert into players(tenant_id, id, account_id, display_name, avatar_seed, lang)
            select @tenantId, players.id, accounts.id, 'Presence player', 'presence', 'en'
            from unnest(@playerIds) with ordinality as players(id, ordinality)
            join unnest(@accountIds) with ordinality as accounts(id, ordinality) using (ordinality);
            """, new { tenantId = Host.TenantId, accountIds, playerIds });
        return playerIds;
    }

    private Task CreatePresenceUpdateLogAsync() => Host.ExecuteOwnerAsync("""
        create table presence_update_log (id integer generated always as identity primary key);
        grant insert on presence_update_log to quesshi_app;
        grant usage, select on sequence presence_update_log_id_seq to quesshi_app;
        create function record_presence_update() returns trigger language plpgsql as $$
        begin
            insert into presence_update_log default values;
            return null;
        end;
        $$;
        create trigger presence_update_statement
            after update of last_seen_at on players
            for each statement execute function record_presence_update();
        """, new { });

    private async Task<int> PresenceUpdateStatementCountAsync() => Assert.Single(await Host.QueryOwnerAsync<int>(
        "select count(*)::integer from presence_update_log", new { }));

    private static async Task<OpenStream> OpenStreamAsync(HttpClient client, Guid playerId, Guid? sessionId = null)
    {
        var path = sessionId is { } watched ? $"/api/stream?session={watched}" : "/api/stream";
        var request = new HttpRequestMessage(HttpMethod.Get, path);
#if QUESSHI_TEST_IDENTITY
        request.Headers.Add("X-Quesshi-Test-Player", playerId.ToString());
#endif
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        return new OpenStream(request, response, new StreamReader(await response.Content.ReadAsStreamAsync(), Encoding.UTF8, leaveOpen: true));
    }

    private static async Task<SseFrame> ReadFrameAsync(StreamReader reader, TimeSpan? timeout = null)
    {
        using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        string? eventName = null;
        string? data = null;
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellation.Token);
            if (line is null) throw new InvalidOperationException("The stream ended before a frame was received.");
            if (line.Length == 0 && eventName == "view" && data is not null) return new SseFrame(eventName, data);
            if (line.StartsWith("event: ", StringComparison.Ordinal)) eventName = line[7..];
            if (line.StartsWith("data: ", StringComparison.Ordinal)) data = line[6..];
        }
    }

    private static bool IsOnline(SseFrame frame, Guid playerId)
    {
        using var document = JsonDocument.Parse(frame.Data);
        return document.RootElement.GetProperty("Roster").GetProperty("Seats").EnumerateArray()
            .Single(seat => seat.GetProperty("Id").GetString() == playerId.ToString()).GetProperty("Online").GetBoolean();
    }

    private static long Version(SseFrame frame)
    {
        using var document = JsonDocument.Parse(frame.Data);
        return document.RootElement.GetProperty("Version").GetInt64();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("The expected stream subscription state was not reached.");
    }

    private static string Code() => $"PRESENCE{Guid.NewGuid():N}"[..16];

    private sealed record SseFrame(string Event, string Data);

    private sealed class OpenStream(HttpRequestMessage request, HttpResponseMessage response, StreamReader reader) : IAsyncDisposable
    {
        public StreamReader Reader { get; } = reader;

        public ValueTask DisposeAsync()
        {
            Reader.Dispose();
            response.Dispose();
            request.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PresenceStreamFeature : IFeature
    {
        public string Id => "stream_presence";
        public FeatureSettings Settings => FeatureSettings.Create(new PresenceStreamSettings(), _ => []);
        public void Configure(IFeatureBuilder feature) { }
    }

    private sealed record PresenceStreamSettings;
}
