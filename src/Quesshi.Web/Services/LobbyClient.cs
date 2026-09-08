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
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private Timer? _timer;

    public event Action<LiveChallengeDto>? ChallengeReceived;
    public event Action<string>? ChallengeExpired;
    public event Action<string>? ChallengeDeclined;
    public event Action<string>? DuelReady;
    public event Action<string>? ChallengeFailed;

    /// <summary>Surfaces <see cref="HubConnection.Closed"/> — fired once the reconnect attempts
    /// (<see cref="ReconnectDelays"/>) are exhausted, or on an explicit <see cref="StopAsync"/>.
    /// A consumer wanting only unintentional drops must track its own stop separately.</summary>
    public event Action? Closed;

    public LobbyClient(string hubUrl, Func<string?> accessTokenProvider)
        : this(BuildConnection(hubUrl, accessTokenProvider))
    {
    }

    /// <summary>Internal seam: lets a test hand in an unstarted <see cref="HubConnection"/> and inspect the wiring without a live socket.</summary>
    internal LobbyClient(HubConnection connection)
    {
        _connection = connection;
        _connection.Reconnected += OnReconnectedAsync;
        _connection.Closed += OnClosedAsync;

        _connection.On<LiveChallengeDto>("ChallengeReceived", c => ChallengeReceived?.Invoke(c));
        _connection.On<string>("ChallengeExpired", id => ChallengeExpired?.Invoke(id));
        _connection.On<string>("ChallengeDeclined", id => ChallengeDeclined?.Invoke(id));
        _connection.On<string>("DuelReady", matchId => DuelReady?.Invoke(matchId));
        _connection.On<string>("ChallengeFailed", id => ChallengeFailed?.Invoke(id));
    }

    internal HubConnection Connection => _connection;

    /// <summary>Whether the underlying connection is currently usable for an invoke.</summary>
    public bool IsConnected => _connection.State == HubConnectionState.Connected;

    /// <summary>Queues the caller for a random live opponent. <see cref="QueueRandomResult.Sent"/> is
    /// false when the invoke could not be sent at all (the connection is not active, or drops mid-call)
    /// — distinct from a sent call whose null <see cref="QueueRandomResult.MatchId"/> means: now waiting,
    /// the duel could not be built, or the caller already holds a pending challenge (<c>QueueFailed</c>
    /// on <see cref="Connection"/> distinguishes the second from the other two).</summary>
    public async Task<QueueRandomResult> QueueRandomAsync(int lang, int questionCount, List<string> categories, List<int> levels)
    {
        try
        {
            var matchId = await _connection.InvokeAsync<string?>("QueueRandom", lang, questionCount, categories, levels);
            return new QueueRandomResult(true, matchId);
        }
        catch
        {
            return new QueueRandomResult(false, null);
        }
    }

    /// <summary>False if the invoke could not be sent — the caller's local "left the queue" state
    /// stands regardless, since there is nothing more useful to do with a dead connection here.</summary>
    public async Task<bool> LeaveQueueAsync()
    {
        try
        {
            await _connection.InvokeAsync("LeaveQueue");
            return true;
        }
        catch
        {
            return false;
        }
    }

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

    /// <summary>Idempotent: returns true immediately if already connected, otherwise attempts a start
    /// and reports whether it succeeded — never throws. A <see cref="SemaphoreSlim"/> serializes
    /// overlapping callers (a button press racing the layout's own sync, say) so at most one
    /// <see cref="StartAsync"/> is ever in flight, which is what keeps a second caller from hitting
    /// <see cref="HubConnection.StartAsync"/>'s own "already starting" exception.</summary>
    public async Task<bool> EnsureConnectedAsync(CancellationToken ct = default)
    {
        if (IsConnected) return true;

        await _startLock.WaitAsync(ct);
        try
        {
            if (IsConnected) return true;
            await StartAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            _startLock.Release();
        }
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

    /// <summary>Stops the now-pointless heartbeat and republishes the drop as <see cref="Closed"/>.</summary>
    internal Task OnClosedAsync(Exception? exception)
    {
        _timer?.Dispose();
        _timer = null;
        Closed?.Invoke();
        return Task.CompletedTask;
    }

    private async Task StartHeartbeatingAsync()
    {
        _timer?.Dispose();
        await HeartbeatAsync();
        _timer = new Timer(_ => _ = HeartbeatAsync(), null, HeartbeatInterval, HeartbeatInterval);
    }

    internal Task HeartbeatAsync()
        => HeartbeatInvokerOverrideForTests is { } overridden ? overridden() : _connection.InvokeAsync("Heartbeat");

    /// <summary>Null means the invoke could not be sent — every real result is a defined <c>LiveChallengeResult</c> value.</summary>
    public async Task<int?> ChallengeAsync(string targetId, string? lang, int questionCount, List<string> categoryIds, List<int> levels, CancellationToken ct = default)
    {
        try
        {
            return await _connection.InvokeAsync<int>("Challenge", targetId, lang, questionCount, categoryIds, levels, ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// <c>Lobby.razor</c>'s own invite-a-friend control: points a plain invitation at a lobby that
    /// already exists (<paramref name="lobbyId"/>) instead of minting a fresh 1v1 the way
    /// <see cref="ChallengeAsync"/> does. Null means the invoke could not be sent — every real result
    /// is a defined <c>LiveChallengeResult</c> value, the same convention <see cref="ChallengeAsync"/>
    /// already follows.
    /// </summary>
    public async Task<int?> InviteToLobbyAsync(string targetId, string lobbyId, CancellationToken ct = default)
    {
        try
        {
            return await _connection.InvokeAsync<int>("InviteToLobby", targetId, lobbyId, ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Null means the invoke could not be sent.</summary>
    public async Task<LiveChallengeAcceptResultDto?> AcceptAsync(string challengeId, CancellationToken ct = default)
    {
        try
        {
            return await _connection.InvokeAsync<LiveChallengeAcceptResultDto>("Accept", challengeId, ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Null means the invoke could not be sent — every real result is a defined <c>LiveChallengeResult</c> value.</summary>
    public async Task<int?> DeclineAsync(string challengeId, CancellationToken ct = default)
    {
        try
        {
            return await _connection.InvokeAsync<int>("Decline", challengeId, ct);
        }
        catch
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _connection.Reconnected -= OnReconnectedAsync;
        _connection.Closed -= OnClosedAsync;
        _timer?.Dispose();
        _startLock.Dispose();
        await _connection.DisposeAsync();
    }
}

/// <summary>The wire shape of <c>LobbyHub.Accept</c>'s return value.</summary>
public sealed record LiveChallengeAcceptResultDto(int Result, string? MatchId, string? ChallengerId);

/// <summary><see cref="LobbyClient.QueueRandomAsync"/>'s outcome. <paramref name="Sent"/> is false only
/// when the invoke itself could not go out — a sent call's own null <paramref name="MatchId"/> is a
/// separate, valid outcome (queued and waiting).</summary>
public readonly record struct QueueRandomResult(bool Sent, string? MatchId);
