using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Plugin.Api;
using Quesshi.Testing;
using Xunit.Sdk;

namespace Quesshi.Server.Tests;

[CollectionDefinition("App-wide stream", DisableParallelization = true)]
public sealed class AppWideStreamCollection;

[Collection("App-wide stream")]
public sealed class AppWideStreamDatabaseTests : IAsyncLifetime
{
    private static readonly bool IsPostgresConfigured = PostgresFixture.IsConfigured(
        Environment.GetEnvironmentVariable(PostgresFixture.AdminConnectionStringEnvironmentVariable),
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase));
    private PlatformHttpFixture? _host;

    public async Task InitializeAsync()
    {
        if (!IsPostgresConfigured) return;
        _host = new PlatformHttpFixture([new StreamTestFeature()]);
        await _host.StartAsync();
        await _host.SetCapabilityAsync("feature", "stream_test", true);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task Unsigned_app_wide_stream_is_rejected_by_the_real_host()
    {
        using var response = await Host.Anonymous.GetAsync("/api/stream");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task App_wide_stream_starts_with_the_players_durable_inbox_snapshot()
    {
        var sessionId = await CommitInvitationAsync(Host.PlayerId);
        await using var stream = await OpenStreamAsync(Host.Player, Host.PlayerId);

        var inbox = await ReadFrameAsync(stream.Reader);

        using var document = JsonDocument.Parse(inbox.Data);
        Assert.Equal("inbox", inbox.Event);
        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt64());
        Assert.Equal(sessionId, document.RootElement
            .GetProperty("invitations").EnumerateArray().Single().GetProperty("sessionId").GetGuid());
    }

    [SkippableFact]
    public async Task App_wide_stream_never_emits_an_initial_session_view()
    {
        var sessionId = Guid.NewGuid();
        await Host.ExecuteOwnerAsync("""
            insert into sessions(tenant_id, id, code, format, owner_id, lang, status, capacity, content, version)
            values (@tenantId, @sessionId, @code, 'async_duel', @playerId, 'en', 'lobby', 2, '{}'::jsonb, 1);
            insert into session_participants(tenant_id, session_id, player_id, seat, role, status, phase, slot, step)
            values (@tenantId, @sessionId, @playerId, 0, 'player', 'active', 'idle', -1, 0);
            """, new { tenantId = Host.TenantId, sessionId, playerId = Host.PlayerId, code = Code() });
        await using var stream = await OpenStreamAsync(Host.Player, Host.PlayerId);
        _ = await ReadFrameAsync(stream.Reader);

        await Host.ExecuteOwnerAsync("select pg_notify('quesshi_session', @payload)", new
        {
            payload = $"{Host.TenantId}:{sessionId}:1:0",
        });

        await AssertNoFrameAsync(stream.Reader);
    }

    [SkippableFact]
    public async Task Invitation_commit_delivers_the_updated_inbox_snapshot_within_two_hundred_milliseconds()
    {
        await using var stream = await OpenStreamAsync(Host.Player, Host.PlayerId);
        _ = await ReadFrameAsync(stream.Reader);
        var started = Stopwatch.StartNew();
        var sessionId = await CommitInvitationAsync(Host.PlayerId);

        var inbox = await ReadFrameAsync(stream.Reader);

        Assert.True(started.Elapsed < TimeSpan.FromMilliseconds(200), $"Inbox delivery took {started.ElapsedMilliseconds}ms.");
        using var document = JsonDocument.Parse(inbox.Data);
        Assert.Equal(sessionId, document.RootElement
            .GetProperty("invitations").EnumerateArray().Single().GetProperty("sessionId").GetGuid());
    }

    [SkippableFact]
    public async Task Inbox_reconciliation_delivers_an_invitation_after_its_notification_is_suppressed()
    {
        await using var stream = await OpenStreamAsync(Host.Player, Host.PlayerId);
        _ = await ReadFrameAsync(stream.Reader);
        var sessionId = Guid.NewGuid();
        await Host.ExecuteOwnerAsync("""
            insert into sessions(tenant_id, id, code, format, owner_id, lang, status, capacity, content)
            values (@tenantId, @sessionId, @code, 'async_duel', @playerId, 'en', 'lobby', 2, '{}'::jsonb);
            insert into session_invites(tenant_id, session_id, player_id, invited_by, expires_at)
            values (@tenantId, @sessionId, @playerId, @playerId, now() + interval '1 hour');
            insert into player_inbox(tenant_id, player_id, version)
            values (@tenantId, @playerId, 1)
            on conflict (tenant_id, player_id) do update set version = player_inbox.version + 1;
            """, new { tenantId = Host.TenantId, sessionId, playerId = Host.PlayerId, code = Code() });

        var inbox = await ReadFrameAsync(stream.Reader, TimeSpan.FromMilliseconds(2500));

        using var document = JsonDocument.Parse(inbox.Data);
        Assert.Equal(sessionId, document.RootElement
            .GetProperty("invitations").EnumerateArray().Single().GetProperty("sessionId").GetGuid());
    }

    [SkippableFact]
    public async Task Capability_commit_sends_the_new_config_version_to_every_open_tenant_stream()
    {
        var before = await ConfigVersionAsync();
        var (secondPlayerClient, secondPlayerId) = await Host.CreateSecondPlayerClientAsync();
        using var secondPlayer = secondPlayerClient;
        await using var firstStream = await OpenStreamAsync(Host.Player, Host.PlayerId);
        await using var secondStream = await OpenStreamAsync(secondPlayer, secondPlayerId);
        _ = await ReadFrameAsync(firstStream.Reader);
        _ = await ReadFrameAsync(secondStream.Reader);

        using var update = await UpdateCapabilityAsync(Host.Owner);
        update.EnsureSuccessStatusCode();
        var first = await ReadFrameAsync(firstStream.Reader);
        var secondFrame = await ReadFrameAsync(secondStream.Reader);

        Assert.Equal("config", first.Event);
        Assert.Equal("config", secondFrame.Event);
        Assert.Equal(before + 1, Version(first));
        Assert.Equal(before + 1, Version(secondFrame));
    }

    [SkippableFact]
    public async Task Capability_commit_sends_no_config_event_to_another_tenants_open_stream()
    {
        var (otherTenantPlayerClient, otherTenantPlayerId) = await Host.CreateOtherTenantPlayerWithIdAsync();
        using var otherTenantPlayer = otherTenantPlayerClient;
        await using var stream = await OpenStreamAsync(otherTenantPlayer, otherTenantPlayerId);
        _ = await ReadFrameAsync(stream.Reader);

        using var update = await UpdateCapabilityAsync(Host.Owner);
        update.EnsureSuccessStatusCode();

        await AssertNoFrameAsync(stream.Reader);
    }

    [SkippableFact]
    public async Task Config_reconciliation_delivers_a_changed_tenant_version_after_its_notification_is_suppressed()
    {
        var before = await ConfigVersionAsync();
        await using var stream = await OpenStreamAsync(Host.Player, Host.PlayerId);
        _ = await ReadFrameAsync(stream.Reader);
        await Host.ExecuteOwnerAsync("update tenants set config_version = config_version + 1 where id = @tenantId", new { tenantId = Host.TenantId });

        var config = await ReadFrameAsync(stream.Reader, TimeSpan.FromMilliseconds(2500));

        Assert.Equal("config", config.Event);
        Assert.Equal(before + 1, Version(config));
    }

    private PlatformHttpFixture Host
    {
        get
        {
            Skip.If(!IsPostgresConfigured, "PostgreSQL is not configured.");
            return _host ?? throw new InvalidOperationException("The platform HTTP host did not start.");
        }
    }

    private async Task<Guid> CommitInvitationAsync(Guid playerId)
    {
        var sessionId = Guid.NewGuid();
        var database = Host.Services.GetRequiredService<ITenantDb>();
        var inbox = Host.Services.GetRequiredService<IInbox>();
        await database.InTenantAsync(Host.TenantId, async transaction =>
        {
            await transaction.ExecuteAsync("""
                insert into sessions(tenant_id, id, code, format, owner_id, lang, status, capacity, content)
                values (current_setting('app.tenant_id')::uuid, @sessionId, @code, 'async_duel', @playerId, 'en', 'lobby', 2, '{}'::jsonb);
                insert into session_invites(tenant_id, session_id, player_id, invited_by, expires_at)
                values (current_setting('app.tenant_id')::uuid, @sessionId, @playerId, @playerId, now() + interval '1 hour');
                """, new { sessionId, playerId, code = Code() });
            inbox.Changed(transaction, playerId);
            return 0;
        });
        return sessionId;
    }

    private static Task<HttpResponseMessage> UpdateCapabilityAsync(HttpClient client) =>
        client.PutAsJsonAsync("/api/admin/capabilities/feature/stream_test", new { enabled = false, settings = new { } });

    private static async Task<OpenStream> OpenStreamAsync(HttpClient client, Guid playerId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/stream");
#if QUESSHI_TEST_IDENTITY
        request.Headers.Add("X-Quesshi-Test-Player", playerId.ToString());
#endif
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        return new OpenStream(request, response, new StreamReader(await response.Content.ReadAsStreamAsync(), Encoding.UTF8, leaveOpen: true));
    }

    private async Task<long> ConfigVersionAsync()
    {
        var versions = await Host.QueryOwnerAsync<long>("select config_version from tenants where id = @tenantId", new { tenantId = Host.TenantId });
        return Assert.Single(versions);
    }

    private static long Version(SseFrame frame)
    {
        using var document = JsonDocument.Parse(frame.Data);
        return document.RootElement.GetProperty("version").GetInt64();
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
            if (line.Length == 0 && eventName is not null && data is not null) return new SseFrame(eventName, data);
            if (line.StartsWith("event: ", StringComparison.Ordinal)) eventName = line[7..];
            if (line.StartsWith("data: ", StringComparison.Ordinal)) data = line[6..];
        }
    }

    private static async Task AssertNoFrameAsync(StreamReader reader)
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadFrameAsync(reader, TimeSpan.FromMilliseconds(400)));
    }

    private static string Code() => $"STREAM{Guid.NewGuid():N}"[..16];

    private sealed record SseFrame(string Event, string Data);

    private sealed class OpenStream : IAsyncDisposable
    {
        private readonly HttpRequestMessage request;
        private readonly HttpResponseMessage response;

        public OpenStream(HttpRequestMessage request, HttpResponseMessage response, StreamReader reader)
        {
            this.request = request;
            this.response = response;
            Reader = reader;
        }

        public StreamReader Reader { get; }

        public ValueTask DisposeAsync()
        {
            Reader.Dispose();
            response.Dispose();
            request.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StreamTestFeature : IFeature
    {
        public string Id => "stream_test";
        public FeatureSettings Settings => FeatureSettings.Create(new StreamTestSettings(), _ => []);
        public void Configure(IFeatureBuilder feature) { }
    }

    private sealed record StreamTestSettings;
}
