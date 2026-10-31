using System.Net;
using System.Net.ServerSentEvents;
using System.Text;

namespace Quesshi.Web.Services;

public enum StreamNotificationKind { View, Inbox, Config, Effects, Gone, Closed }

/// <summary>One raw server event. Snapshot ordering and versioning belong to the state consumer.</summary>
public sealed record StreamNotification(StreamNotificationKind Kind, string Data);

/// <summary>
/// Shell-owned SSE transport: at most one GET /api/stream reader, started after player sign-in,
/// restarted when the watched session changes and stopped at sign-out.
/// </summary>
public sealed class StreamClient(ClientHttp http, TimeProvider? timeProvider = null) : IAsyncDisposable
{
    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(30);

    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);
    private CancellationTokenSource? readerCancellation;
    private Task reader = Task.CompletedTask;
    private bool running;
    private Guid? runningSession;

    public event Action<StreamNotification>? Received;

    /// <summary>Raised when the stream answers 401; the shell signs the player out.</summary>
    public event Action? Unauthorized;

    /// <summary>Starts, restarts or stops the reader so it matches the desired state; a no-op when it already does.</summary>
    public Task SyncAsync(bool signedIn, Guid? session, CancellationToken cancellationToken = default) =>
        SyncAsync(() => (signedIn, session), cancellationToken);

    /// <summary>Like <see cref="SyncAsync(bool, Guid?, CancellationToken)"/>, reading the desired state only once queued calls ahead have finished.</summary>
    public async Task SyncAsync(Func<(bool SignedIn, Guid? Session)> desired, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var (signedIn, session) = desired();
            if (running == signedIn && (!signedIn || runningSession == session)) return;
            await StopReaderAsync();
            if (!signedIn) return;
            running = true;
            runningSession = session;
            var cancellation = readerCancellation = new CancellationTokenSource();
            reader = Task.Run(() => RunAsync(session, cancellation.Token), CancellationToken.None);
        }
        finally { gate.Release(); }
    }

    public static string PathFor(Guid? session) => session is { } id ? $"/api/stream?session={id}" : "/api/stream";

    public static TimeSpan RetryDelay(int attempt, double jitter)
    {
        var exponential = Math.Min(MaximumDelay.TotalMilliseconds, BaseDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt, 16)));
        return TimeSpan.FromMilliseconds(exponential * (0.5 + 0.5 * Math.Clamp(jitter, 0, 1)));
    }

    private async Task StopReaderAsync()
    {
        var cancellation = readerCancellation;
        readerCancellation = null;
        running = false;
        runningSession = null;
        if (cancellation is null) return;
        await cancellation.CancelAsync();
        try { await reader; }
        catch (OperationCanceledException) { }
        finally { cancellation.Dispose(); }
    }

    private async Task RunAsync(Guid? session, CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var response = await http.OpenStreamAsync(PathFor(session), cancellationToken);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    Unauthorized?.Invoke();
                    return;
                }

                if (response.StatusCode == HttpStatusCode.Forbidden || ClientHttp.IsOutdated(response)) return;
                if (response.IsSuccessStatusCode && IsEventStream(response))
                {
                    var terminal = await ReadAsync(response, () => attempt = 0, cancellationToken);
                    if (terminal) return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException) { }

            try { await Task.Delay(RetryDelay(attempt++, Random.Shared.NextDouble()), time, cancellationToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static bool IsEventStream(HttpResponseMessage response) =>
        string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads frames until the server ends the response; true when the server ended it deliberately (gone/closed).</summary>
    private async Task<bool> ReadAsync(HttpResponseMessage response, Action connected, CancellationToken cancellationToken)
    {
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        connected();
        var parser = SseParser.Create(body, (type, data) => Encoding.UTF8.GetString(data));
        await foreach (var item in parser.EnumerateAsync(cancellationToken))
        {
            if (!TryKind(item.EventType, out var kind)) continue;
            Received?.Invoke(new StreamNotification(kind, item.Data));
            if (kind is StreamNotificationKind.Gone or StreamNotificationKind.Closed) return true;
        }

        return false;
    }

    private static bool TryKind(string type, out StreamNotificationKind kind)
    {
        switch (type)
        {
            case "view": kind = StreamNotificationKind.View; return true;
            case "inbox": kind = StreamNotificationKind.Inbox; return true;
            case "config": kind = StreamNotificationKind.Config; return true;
            case "effects": kind = StreamNotificationKind.Effects; return true;
            case "gone": kind = StreamNotificationKind.Gone; return true;
            case "closed": kind = StreamNotificationKind.Closed; return true;
            default: kind = default; return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { await StopReaderAsync(); }
        finally { gate.Release(); }
    }
}
