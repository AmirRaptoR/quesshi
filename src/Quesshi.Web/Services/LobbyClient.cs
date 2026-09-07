using Microsoft.AspNetCore.SignalR.Client;
using Quesshi.Shared;

namespace Quesshi.Web.Services;

/// <summary>
/// One connection for the whole session — unlike <see cref="LiveClient"/>, which is scoped to a
/// single duel. Opened once the player is signed in and not a guest, held until they leave, and
/// heartbeats on a timer the whole time so the presence key and any queue entry on the server stay
/// alive. Also carries friend challenge events, so an invitation arrives wherever the player is in
/// the app rather than on one page.
/// </summary>
public sealed class LobbyClient : IAsyncDisposable
{
    private static readonly TimeSpan[] ReconnectDelays =
        [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    /// <summary>A third of <c>LobbyHub.PresenceTtl</c>, so two dropped beats don't flicker the player offline.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);

    private readonly HubConnection _connection;
    private Timer? _timer;

    public event Action<LiveChallengeDto>? ChallengeReceived;
    public event Action<string>? ChallengeExpired;
    public event Action<string>? ChallengeDeclined;
    public event Action<string>? DuelReady;
    public event Action<string>? ChallengeFailed;

    public LobbyClient(string hubUrl, Func<string?> accessTokenProvider)
        : this(BuildConnection(hubUrl, accessTokenProvider))
    {
    }

    /// <summary>Internal seam: lets a test hand in an unstarted <see cref="HubConnection"/> and inspect the wiring without a live socket.</summary>
    internal LobbyClient(HubConnection connection)
    {
        _connection = connection;
        _connection.Reconnected += OnReconnectedAsync;

        _connection.On<LiveChallengeDto>("ChallengeReceived", c => ChallengeReceived?.Invoke(c));
        _connection.On<string>("ChallengeExpired", id => ChallengeExpired?.Invoke(id));
        _connection.On<string>("ChallengeDeclined", id => ChallengeDeclined?.Invoke(id));
        _connection.On<string>("DuelReady", matchId => DuelReady?.Invoke(matchId));
        _connection.On<string>("ChallengeFailed", id => ChallengeFailed?.Invoke(id));
    }

    internal HubConnection Connection => _connection;

    /// <summary>Queues the caller for a random live opponent. Null means: now waiting, the duel
    /// could not be built, or the caller already holds a pending challenge — <c>QueueFailed</c> on
    /// <see cref="Connection"/> distinguishes the second from the other two.</summary>
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

    public Task<int> ChallengeAsync(string targetId, string? lang, int questionCount, List<string> categoryIds, List<int> levels, CancellationToken ct = default)
        => _connection.InvokeAsync<int>("Challenge", targetId, lang, questionCount, categoryIds, levels, ct);

    public Task<LiveChallengeAcceptResultDto> AcceptAsync(string challengeId, CancellationToken ct = default)
        => _connection.InvokeAsync<LiveChallengeAcceptResultDto>("Accept", challengeId, ct);

    public Task<int> DeclineAsync(string challengeId, CancellationToken ct = default)
        => _connection.InvokeAsync<int>("Decline", challengeId, ct);

    public async ValueTask DisposeAsync()
    {
        _connection.Reconnected -= OnReconnectedAsync;
        _timer?.Dispose();
        await _connection.DisposeAsync();
    }
}

/// <summary>The wire shape of <c>LobbyHub.Accept</c>'s return value.</summary>
public sealed record LiveChallengeAcceptResultDto(int Result, string? MatchId, string? ChallengerId);
