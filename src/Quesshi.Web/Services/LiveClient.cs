using Microsoft.AspNetCore.SignalR.Client;
using Quesshi.Shared;

namespace Quesshi.Web.Services;

/// <summary>
/// Wraps one live-duel <see cref="HubConnection"/>: connects with the bearer token, exposes the
/// server's pushes as typed events over the <see cref="Quesshi.Shared"/> DTOs, reconnects with a
/// bounded backoff, and re-<see cref="JoinAsync"/>s on every reconnect so a page never has to notice
/// the drop. One instance per live duel — a page owns it and disposes it when the duel is over.
/// </summary>
public sealed class LiveClient : IAsyncDisposable
{
    private static readonly TimeSpan[] ReconnectDelays =
        [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    private readonly HubConnection _connection;
    private string? _matchId;

    public event Action<LiveRoundCardDto>? RoundStarted;
    public event Action<LiveRoundRevealDto>? RoundRevealed;
    public event Action<LiveEndedDto>? Ended;
    public event Action<OpponentPresenceDto>? OpponentLeft;
    public event Action<OpponentPresenceDto>? OpponentBack;

    /// <summary>
    /// Server time minus local time, captured once from the <c>ServerNow</c> of the first
    /// <see cref="LiveViewDto"/> a connection receives. Because every deadline the hub sends is
    /// absolute server time, this is the only clock information a reconnect ever needs — there is no
    /// other path here that asks the server for the time.
    /// </summary>
    public TimeSpan Skew { get; private set; }

    public LiveClient(string hubUrl, Func<string?> accessTokenProvider)
        : this(BuildConnection(hubUrl, accessTokenProvider))
    {
    }

    /// <summary>Internal seam: lets a test hand in an unstarted <see cref="HubConnection"/> and inspect the wiring without a live socket.</summary>
    internal LiveClient(HubConnection connection)
    {
        _connection = connection;
        _connection.Reconnected += OnReconnectedAsync;

        _connection.On<LiveRoundCardDto>("RoundStarted", card => RoundStarted?.Invoke(card));
        _connection.On<LiveRoundRevealDto>("RoundRevealed", reveal => RoundRevealed?.Invoke(reveal));
        _connection.On<LiveEndedDto>("Ended", ended => Ended?.Invoke(ended));
        _connection.On<OpponentPresenceDto>("OpponentLeft", p => OpponentLeft?.Invoke(p));
        _connection.On<OpponentPresenceDto>("OpponentBack", p => OpponentBack?.Invoke(p));
    }

    internal HubConnection Connection => _connection;

    /// <summary>Test-only override for the "Join" invocation, so reconnect's rejoin can be proven with no server to call.</summary>
    internal Func<string, Task<LiveViewDto>>? JoinInvokerOverrideForTests { get; set; }

    private static HubConnection BuildConnection(string hubUrl, Func<string?> accessTokenProvider)
        => new HubConnectionBuilder()
            .WithUrl(hubUrl, options => options.AccessTokenProvider = () => Task.FromResult(accessTokenProvider()))
            .WithAutomaticReconnect(ReconnectDelays)
            .Build();

    public Task StartAsync(CancellationToken ct = default) => _connection.StartAsync(ct);

    /// <summary>Joins the match's group and returns the catch-up view. Called again automatically on every reconnect.</summary>
    public async Task<LiveViewDto> JoinAsync(string matchId, CancellationToken ct = default)
    {
        _matchId = matchId;
        var view = JoinInvokerOverrideForTests is { } overriddenJoin
            ? await overriddenJoin(matchId)
            : await _connection.InvokeAsync<LiveViewDto>("Join", matchId, ct);

        Skew = ComputeSkew(view.ServerNow, DateTimeOffset.UtcNow);
        return view;
    }

    public Task LeaveAsync(CancellationToken ct = default)
    {
        var matchId = _matchId;
        _matchId = null;
        return matchId is null ? Task.CompletedTask : _connection.InvokeAsync("Leave", matchId, ct);
    }

    public Task AnswerAsync(string matchId, int round, int choiceIndex, CancellationToken ct = default)
        => _connection.InvokeAsync("Answer", matchId, round, choiceIndex, ct);

    /// <summary>The reconnected handler: without this, a page that survives a drop would be stuck on stale state forever.</summary>
    internal Task OnReconnectedAsync(string? connectionId)
        => _matchId is { } id ? JoinAsync(id) : Task.CompletedTask;

    /// <summary>Pure function so it is testable with no connection at all: server time minus local time.</summary>
    public static TimeSpan ComputeSkew(DateTimeOffset serverNow, DateTimeOffset localNow) => serverNow - localNow;

    public async ValueTask DisposeAsync()
    {
        _connection.Reconnected -= OnReconnectedAsync;
        await _connection.DisposeAsync();
    }
}
