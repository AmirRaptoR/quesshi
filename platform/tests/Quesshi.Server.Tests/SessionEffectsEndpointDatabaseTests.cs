using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Infrastructure.Streaming;
using Quesshi.Plugin.Api;
using Quesshi.Testing;
using Xunit.Sdk;

namespace Quesshi.Server.Tests;

public sealed class SessionEffectsEndpointDatabaseTests(PostgresFixture postgres) : IAsyncLifetime, IClassFixture<PostgresFixture>
{
    private PlatformHttpFixture? _host;
    private EffectsHttpFeature? _feature;

    public async Task InitializeAsync()
    {
        if (postgres.ConnectionString is null) return;
        _feature = new EffectsHttpFeature();
        _host = new PlatformHttpFixture([_feature]);
        await _host.StartAsync();
        await Host.SetCapabilityAsync("feature", _feature.Id, true);
        await WaitForAsync(() => Host.Services.GetRequiredService<PostgresStreamNotificationListener>().BackendProcessId != 0);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task Unsigned_effects_request_is_rejected_by_the_real_host()
    {
        using var response = await Host.Anonymous.GetAsync($"/api/sessions/{Guid.NewGuid()}/effects");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task Another_tenant_cannot_read_a_plausible_finished_sessions_effects()
    {
        var sessionId = await CreateFinishedSessionAsync();
        using var otherTenant = await Host.CreateOtherTenantPlayerClientAsync();
        using var response = await otherTenant.GetAsync($"/api/sessions/{sessionId}/effects");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal((HttpStatusCode.NotFound, "{\"error\":\"not_found\"}"), (response.StatusCode, body));
    }

    [SkippableFact]
    public async Task Finished_session_reports_an_off_effect_for_a_disabled_feature()
    {
        await Host.SetCapabilityAsync("feature", Feature.Id, false);
        var sessionId = await CreateFinishedSessionAsync();
        using var response = await Host.Player.GetAsync($"/api/sessions/{sessionId}/effects");
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, document.RootElement.GetProperty("revision").GetInt64());
        var effect = document.RootElement.GetProperty("features").EnumerateArray().Single();
        Assert.Equal(("effects_http", "off"), (effect.GetProperty("featureId").GetString(), effect.GetProperty("status").GetString()));
    }

    [SkippableFact]
    public async Task Finished_session_watcher_receives_the_pending_effects_snapshot_returned_by_the_api()
    {
        var sessionId = await CreateFinishedSessionAsync();
        await EmitPendingSessionEndedAsync(sessionId);
        try
        {
            using var response = await Host.Player.GetAsync($"/api/sessions/{sessionId}/effects");
            var expected = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await using var stream = await OpenStreamAsync(sessionId);
            var frame = await ReadEffectsFrameAsync(stream.Reader);

            using var document = JsonDocument.Parse(frame.Data);
            Assert.Equal(expected, document.RootElement.GetRawText());
            Assert.Equal("pending", document.RootElement.GetProperty("features").EnumerateArray().Single().GetProperty("status").GetString());
        }
        finally { Feature.ReleaseHandler(); }
    }

    [SkippableFact]
    public async Task Handler_commit_delivers_the_done_effects_revision_through_postgres_notification()
    {
        var sessionId = await CreateFinishedSessionAsync();
        await EmitPendingSessionEndedAsync(sessionId);
        await using var stream = await OpenStreamAsync(sessionId);
        var pending = await ReadEffectsFrameAsync(stream.Reader);
        var pendingRevision = Revision(pending);
        var listener = Host.Services.GetRequiredService<PostgresStreamNotificationListener>();
        var notified = new List<StreamNotification.Effects>();
        void Record(StreamNotification notification)
        {
            if (notification is StreamNotification.Effects effects && effects.TenantId == Host.TenantId && effects.SessionId == sessionId)
                lock (notified) notified.Add(effects);
        }

        // The listener's notification for this session proves the delivery path; a wall-clock
        // limit failed on slow CI runners while the notification path itself worked.
        listener.Received += Record;
        try
        {
            Feature.ReleaseHandler();
            var done = await ReadEffectsFrameAsync(stream.Reader);

            Assert.Equal("done", Status(done));
            Assert.True(Revision(done) > pendingRevision);
            lock (notified) Assert.NotEmpty(notified);
        }
        finally
        {
            listener.Received -= Record;
        }
    }

    [SkippableFact]
    public async Task Reconciliation_delivers_the_done_effects_revision_after_the_listener_misses_its_notification()
    {
        var sessionId = await CreateFinishedSessionAsync();
        await EmitPendingSessionEndedAsync(sessionId);
        await using var stream = await OpenStreamAsync(sessionId);
        var pending = await ReadEffectsFrameAsync(stream.Reader);
        var pendingRevision = Revision(pending);
        var listener = Host.Services.GetRequiredService<PostgresStreamNotificationListener>();
        var missed = new List<StreamNotification.Effects>();
        void Record(StreamNotification notification)
        {
            if (notification is StreamNotification.Effects effects && effects.TenantId == Host.TenantId && effects.SessionId == sessionId)
                missed.Add(effects);
        }

        listener.Received += Record;
        await listener.StopAsync(CancellationToken.None);
        await WaitForAsync(() => listener.BackendProcessId == 0);
        try
        {
            var deadline = TimeSpan.FromMilliseconds(2500);
            var started = Stopwatch.StartNew();
            Feature.ReleaseHandler();
            await WaitForEffectsStatusAsync(sessionId, "done", Remaining(deadline, started));
            Assert.Empty(missed);

            await listener.StartAsync(CancellationToken.None);
            await WaitForAsync(() => listener.BackendProcessId != 0, Remaining(deadline, started));
            var done = await ReadEffectsFrameAsync(stream.Reader, Remaining(deadline, started));

            Assert.True(started.Elapsed < deadline, $"Effects reconciliation took {started.ElapsedMilliseconds}ms.");
            Assert.Equal("done", Status(done));
            Assert.True(Revision(done) > pendingRevision);
        }
        finally
        {
            listener.Received -= Record;
        }
    }

    [SkippableFact]
    public async Task Dead_effect_job_delivers_the_failed_plugin_state_to_the_finished_session_watcher()
    {
        var sessionId = await CreateFinishedSessionAsync();
        await EmitPendingSessionEndedAsync(sessionId);
        await using var stream = await OpenStreamAsync(sessionId);
        _ = await ReadEffectsFrameAsync(stream.Reader);
        Feature.FailNextHandler();
        await Host.ExecuteOwnerAsync("update jobs set max_attempts = 1 where tenant_id = @tenantId and kind = 'event'", new { tenantId = Host.TenantId });

        Feature.ReleaseHandler();
        var failed = await ReadEffectsFrameAsync(stream.Reader);

        Assert.Equal("failed", Status(failed));
    }

    private PlatformHttpFixture Host
    {
        get
        {
            Skip.If(postgres.ConnectionString is null, "PostgreSQL is not configured.");
            return _host ?? throw new InvalidOperationException("The platform HTTP host did not start.");
        }
    }

    private async Task<Guid> CreateFinishedSessionAsync()
    {
        var sessionId = Guid.NewGuid();
        await Host.ExecuteOwnerAsync("""
            insert into sessions(tenant_id, id, code, format, owner_id, lang, status, capacity, content, phase, started_at, ended_at)
            values (@tenantId, @sessionId, @code, 'async_duel', @playerId, 'en', 'finished', 1, '{}'::jsonb, '0', now(), now());
            insert into session_participants(tenant_id, session_id, player_id, seat)
            values (@tenantId, @sessionId, @playerId, 0);
            """, new { tenantId = Host.TenantId, sessionId, playerId = Host.PlayerId, code = sessionId.ToString("N")[..8] });
        return sessionId;
    }

    private EffectsHttpFeature Feature => _feature ?? throw new InvalidOperationException("The effects feature did not initialize.");

    private async Task EmitSessionEndedAsync(Guid sessionId)
    {
        var database = Host.Services.GetRequiredService<ITenantDb>();
        var events = Host.Services.GetRequiredService<IEvents>();
        await database.InTenantAsync(Host.TenantId, async transaction =>
        {
            await events.EmitAsync(transaction, new SessionEnded(sessionId));
            return 0;
        });
    }

    private async Task EmitPendingSessionEndedAsync(Guid sessionId)
    {
        Feature.PauseNextHandler();
        await EmitSessionEndedAsync(sessionId);
        await Feature.WaitForHandlerAsync();
    }

    private async Task<OpenStream> OpenStreamAsync(Guid sessionId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/stream?session={sessionId}");
#if QUESSHI_TEST_IDENTITY
        request.Headers.Add("X-Quesshi-Test-Player", Host.PlayerId.ToString());
#endif
        var response = await Host.Player.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        return new OpenStream(request, response, new StreamReader(await response.Content.ReadAsStreamAsync(), Encoding.UTF8, leaveOpen: true));
    }

    private static async Task<SseFrame> ReadEffectsFrameAsync(StreamReader reader, TimeSpan? timeout = null)
    {
        using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            var frame = await ReadFrameAsync(reader, cancellation.Token);
            if (frame.Event == "effects") return frame;
        }
    }

    private static async Task<SseFrame> ReadFrameAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        string? eventName = null;
        string? data = null;
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) throw new InvalidOperationException("The stream ended before an effects frame arrived.");
            if (line.Length == 0 && eventName is not null && data is not null) return new SseFrame(eventName, data);
            if (line.StartsWith("event: ", StringComparison.Ordinal)) eventName = line[7..];
            if (line.StartsWith("data: ", StringComparison.Ordinal)) data = line[6..];
        }
    }

    private static long Revision(SseFrame frame)
    {
        using var document = JsonDocument.Parse(frame.Data);
        return document.RootElement.GetProperty("revision").GetInt64();
    }

    private static string? Status(SseFrame frame)
    {
        using var document = JsonDocument.Parse(frame.Data);
        return document.RootElement.GetProperty("features").EnumerateArray().Single().GetProperty("status").GetString();
    }

    private async Task WaitForEffectsStatusAsync(Guid sessionId, string expectedStatus, TimeSpan? timeout = null)
    {
        var started = Stopwatch.GetTimestamp();
        var timeLimit = timeout ?? TimeSpan.FromSeconds(10);
        while (true)
        {
            using var response = await Host.Player.GetAsync($"/api/sessions/{sessionId}/effects");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var status = document.RootElement.GetProperty("features").EnumerateArray().Single().GetProperty("status").GetString();
            if (status == expectedStatus) return;
            if (Stopwatch.GetElapsedTime(started) >= timeLimit) throw new TimeoutException("The effects handler did not commit its state.");
            await Task.Delay(10);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var started = Stopwatch.GetTimestamp();
        var timeLimit = timeout ?? TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(started) >= timeLimit) throw new TimeoutException("The PostgreSQL stream listener did not reach the expected state.");
            await Task.Delay(10);
        }
    }

    private static TimeSpan Remaining(TimeSpan deadline, Stopwatch started)
    {
        var remaining = deadline - started.Elapsed;
        if (remaining <= TimeSpan.Zero) throw new TimeoutException("Effects reconciliation exceeded its deadline.");
        return remaining;
    }

    private sealed class EffectsHttpFeature : IFeature
    {
        private TaskCompletionSource? _handlerStarted;
        private TaskCompletionSource? _handlerRelease;
        private int _failNext;

        public string Id => "effects_http";
        public FeatureSettings Settings => FeatureSettings.Create(new EffectsHttpSettings(), _ => []);

        public void Configure(IFeatureBuilder feature) => feature.On<SessionEnded>(HandleSessionEndedAsync);

        public void PauseNextHandler()
        {
            _handlerStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _handlerRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Task WaitForHandlerAsync() => (_handlerStarted ?? throw new InvalidOperationException("The handler was not paused."))
            .Task.WaitAsync(TimeSpan.FromSeconds(10));

        public void ReleaseHandler() => (_handlerRelease ?? throw new InvalidOperationException("The handler was not paused.")).TrySetResult();

        public void FailNextHandler() => Interlocked.Exchange(ref _failNext, 1);

        private async Task HandleSessionEndedAsync(ITenantTx transaction, SessionEnded @event, CancellationToken cancellationToken)
        {
            _handlerStarted?.TrySetResult();
            if (_handlerRelease is { } release) await release.Task.WaitAsync(cancellationToken);
            if (Interlocked.Exchange(ref _failNext, 0) == 1) throw new InvalidOperationException("sample effects handler failure");
        }
    }

    private sealed record EffectsHttpSettings;

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
}
