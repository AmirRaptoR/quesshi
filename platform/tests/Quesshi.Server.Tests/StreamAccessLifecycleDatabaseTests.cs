using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quesshi.Infrastructure.Streaming;
using Quesshi.Plugin.Api;
using Quesshi.Testing;
using Xunit.Sdk;

namespace Quesshi.Server.Tests;

public sealed class StreamAccessLifecycleDatabaseTests : IAsyncLifetime
{
    private static readonly bool IsPostgresConfigured = PostgresFixture.IsConfigured(
        Environment.GetEnvironmentVariable(PostgresFixture.AdminConnectionStringEnvironmentVariable),
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase));
    private PlatformHttpFixture? _host;

    public async Task InitializeAsync()
    {
        if (!IsPostgresConfigured) return;
        _host = new PlatformHttpFixture([new StreamLifecycleFeature()]);
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task Signing_out_closes_another_tab_stream_with_signed_out()
    {
        await using var stream = await OpenStreamAsync(Host.Player, Host.PlayerId);
        _ = await ReadFrameAsync(stream.Reader);
        var elapsed = Stopwatch.StartNew();

        using var signedOut = await Host.Player.PostAsync("/api/auth/sign-out", null);
        signedOut.EnsureSuccessStatusCode();
        var closed = await ReadFrameAsync(stream.Reader, TimeSpan.FromMilliseconds(2500));

        Assert.True(elapsed.Elapsed < TimeSpan.FromMilliseconds(2500), $"Stream closure took {elapsed.ElapsedMilliseconds}ms.");
        Assert.Equal(("closed", "signed_out"), (closed.Event, Reason(closed)));
    }

    [SkippableFact]
    public async Task Reconciliation_closes_a_signed_out_stream_when_the_notification_is_suppressed()
    {
        await using var stream = await OpenStreamAsync(Host.Player, Host.PlayerId);
        _ = await ReadFrameAsync(stream.Reader);
        await Host.Services.GetRequiredService<PostgresStreamNotificationListener>().StopAsync(CancellationToken.None);
        var elapsed = Stopwatch.StartNew();

        using var signedOut = await Host.Player.PostAsync("/api/auth/sign-out", null);
        signedOut.EnsureSuccessStatusCode();
        var closed = await ReadFrameAsync(stream.Reader, TimeSpan.FromMilliseconds(2500));

        Assert.True(elapsed.Elapsed < TimeSpan.FromMilliseconds(2500), $"Stream closure took {elapsed.ElapsedMilliseconds}ms.");
        Assert.Equal(("closed", "signed_out"), (closed.Event, Reason(closed)));
    }

    [SkippableFact]
    public async Task Reconciliation_closes_an_expired_authentication_session_with_expired()
    {
        await using var stream = await OpenStreamAsync(Host.Player, Host.PlayerId);
        _ = await ReadFrameAsync(stream.Reader);
        var elapsed = Stopwatch.StartNew();
        await Host.ExecuteOwnerAsync("""
            update auth_sessions
            set expires_at = now() - interval '1 second'
            where tenant_id = @tenantId
              and account_id = (select account_id from players where id = @playerId)
            """, new { tenantId = Host.TenantId, playerId = Host.PlayerId });

        var closed = await ReadFrameAsync(stream.Reader, TimeSpan.FromMilliseconds(2500));

        Assert.True(elapsed.Elapsed < TimeSpan.FromMilliseconds(2500), $"Stream closure took {elapsed.ElapsedMilliseconds}ms.");
        Assert.Equal(("closed", "expired"), (closed.Event, Reason(closed)));
    }

    [SkippableFact]
    public async Task Banning_a_player_closes_the_open_stream_with_banned()
    {
        await using var stream = await OpenStreamAsync(Host.Player, Host.PlayerId);
        _ = await ReadFrameAsync(stream.Reader);
        var elapsed = Stopwatch.StartNew();

        using var banned = await Host.Moderator.PostAsync($"/api/admin/players/{Host.PlayerId}/ban", null);
        banned.EnsureSuccessStatusCode();
        var closed = await ReadFrameAsync(stream.Reader, TimeSpan.FromMilliseconds(2500));

        Assert.True(elapsed.Elapsed < TimeSpan.FromMilliseconds(2500), $"Stream closure took {elapsed.ElapsedMilliseconds}ms.");
        Assert.Equal(("closed", "banned"), (closed.Event, Reason(closed)));
    }

    [SkippableFact]
    public async Task A_banned_player_cannot_reopen_the_stream()
    {
        using var banned = await Host.Moderator.PostAsync($"/api/admin/players/{Host.PlayerId}/ban", null);
        banned.EnsureSuccessStatusCode();

        using var reopened = await Host.Player.GetAsync("/api/stream");

        Assert.Equal(HttpStatusCode.Unauthorized, reopened.StatusCode);
    }

    [SkippableFact]
    public async Task Deleting_an_account_closes_the_open_stream_with_deleted()
    {
        await using var stream = await OpenStreamAsync(Host.Player, Host.PlayerId);
        _ = await ReadFrameAsync(stream.Reader);
        var elapsed = Stopwatch.StartNew();

        using var deleted = await Host.Player.DeleteAsync("/api/me");
        deleted.EnsureSuccessStatusCode();
        var closed = await ReadFrameAsync(stream.Reader, TimeSpan.FromMilliseconds(2500));

        Assert.True(elapsed.Elapsed < TimeSpan.FromMilliseconds(2500), $"Stream closure took {elapsed.ElapsedMilliseconds}ms.");
        Assert.Equal(("closed", "deleted"), (closed.Event, Reason(closed)));
    }

    [SkippableFact]
    public async Task A_deleted_player_cannot_reopen_the_stream()
    {
        using var deleted = await Host.Player.DeleteAsync("/api/me");
        deleted.EnsureSuccessStatusCode();

        using var reopened = await Host.Player.GetAsync("/api/stream");

        Assert.Equal(HttpStatusCode.Unauthorized, reopened.StatusCode);
    }

    [SkippableFact]
    public async Task Successful_guest_upgrade_closes_the_revoked_guest_stream_with_signed_out()
    {
        await using var stream = await OpenStreamAsync(Host.Guest, Guid.Empty);
        _ = await ReadFrameAsync(stream.Reader);
        const string email = "stream-upgrade@example.test";
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes("platform-test-email-auth-secret"), Encoding.UTF8.GetBytes("123456"));
        await Host.ExecuteOwnerAsync("""
            insert into login_codes(email, code_hash, expires_at)
            values (@email, @hash, now() + interval '10 minutes')
            """, new { email, hash });

        using var upgraded = await Host.Guest.PostAsJsonAsync("/api/me/upgrade", new { email, code = "123456" });
        upgraded.EnsureSuccessStatusCode();
        var closed = await ReadFrameAsync(stream.Reader, TimeSpan.FromMilliseconds(2500));

        Assert.Equal(("closed", "signed_out"), (closed.Event, Reason(closed)));
    }

    private PlatformHttpFixture Host
    {
        get
        {
#if QUESSHI_TEST_IDENTITY
            Skip.If(true, "Authentication-session lifecycle requires the production identity resolver.");
#endif
            Skip.If(!IsPostgresConfigured, "PostgreSQL is not configured.");
            return _host ?? throw new InvalidOperationException("The platform HTTP host did not start.");
        }
    }

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

    private static string? Reason(SseFrame frame)
    {
        using var document = JsonDocument.Parse(frame.Data);
        return document.RootElement.GetProperty("reason").GetString();
    }

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

    private sealed class StreamLifecycleFeature : IFeature
    {
        public string Id => "stream_lifecycle";
        public FeatureSettings Settings => FeatureSettings.Create(new StreamLifecycleSettings(), _ => []);
        public void Configure(IFeatureBuilder feature) { }
    }

    private sealed record StreamLifecycleSettings;
}
