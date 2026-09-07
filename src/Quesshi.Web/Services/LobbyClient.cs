using Microsoft.AspNetCore.SignalR.Client;

namespace Quesshi.Web.Services;

/// <summary>
/// One connection for the whole session — unlike <see cref="LiveClient"/>, which is scoped to a
/// single duel. Opened once the app is ready and the player is signed in and not a guest, held until
/// they leave, and heartbeats on a timer the whole time so the presence key on the server stays alive.
/// </summary>
public sealed class LobbyClient : IAsyncDisposable
{
    private static readonly TimeSpan[] ReconnectDelays =
        [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    /// <summary>A third of <c>LobbyHub.PresenceTtl</c>, so two dropped beats don't flicker the player offline.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);

    private readonly HubConnection _connection;
    private Timer? _timer;

    public LobbyClient(string hubUrl, Func<string?> accessTokenProvider)
        : this(BuildConnection(hubUrl, accessTokenProvider))
    {
    }

    /// <summary>Internal seam: lets a test hand in an unstarted <see cref="HubConnection"/> and inspect the wiring without a live socket.</summary>
    internal LobbyClient(HubConnection connection)
    {
        _connection = connection;
        _connection.Reconnected += OnReconnectedAsync;
    }

    internal HubConnection Connection => _connection;

    /// <summary>Queues the caller for a random live opponent. Null means: now waiting, or the duel
    /// could not be built — <c>QueueFailed</c> on <see cref="Connection"/> is what distinguishes those.</summary>
    public Task<string?> QueueRandomAsync(int lang, int questionCount, List<string> categories, List<int> levels)
        => _connection.InvokeAsync<string?>("QueueRandom", lang, questionCount, categories, levels);

    public Task LeaveQueueAsync() => _connection.InvokeAsync("LeaveQueue");

    /// <summary>Test-only override for the "Heartbeat" invocation, so resuming after a reconnect can be proven with no server to call.</summary>
    internal Func<Task>? HeartbeatInvokerOverrideForTests { get; set; }

    private static HubConnection BuildConnection(string hubUrl, Func<string?> accessTokenProvider)
        => new HubConnectionBuilder()
            .WithUrl(hubUrl, options => options.AccessTokenProvider = () => Task.FromResult(accessTokenProvider()))
            .WithAutomaticReconnect(ReconnectDelays)
            .Build();

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _connection.StartAsync(ct);
        await StartHeartbeatingAsync();
    }

    /// <summary>Stops the connection without disposing it, so the same instance can be started again — a
    /// player can sign out and back in within one WASM session without losing this singleton.</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        _timer?.Dispose();
        _timer = null;
        await _connection.StopAsync(ct);
    }

    /// <summary>Without this, a session that survives a drop would stop heartbeating forever and quietly time out.</summary>
    internal Task OnReconnectedAsync(string? connectionId) => StartHeartbeatingAsync();

    private async Task StartHeartbeatingAsync()
    {
        _timer?.Dispose();
        await HeartbeatAsync();
        _timer = new Timer(_ => _ = HeartbeatAsync(), null, HeartbeatInterval, HeartbeatInterval);
    }

    internal Task HeartbeatAsync()
        => HeartbeatInvokerOverrideForTests is { } overridden ? overridden() : _connection.InvokeAsync("Heartbeat");

    public async ValueTask DisposeAsync()
    {
        _connection.Reconnected -= OnReconnectedAsync;
        _timer?.Dispose();
        await _connection.DisposeAsync();
    }
}
