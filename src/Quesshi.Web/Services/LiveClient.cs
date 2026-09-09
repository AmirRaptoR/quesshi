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

    /// <summary>Set by <see cref="JoinAsyncLobbyAsync"/>, cleared by <see cref="LeaveAsync"/> — which
    /// hub method a reconnect's rejoin (<see cref="OnReconnectedAsync"/>) and a deliberate leave
    /// invoke depends on this, since an async lobby has no <c>Join</c>/catch-up view to rejoin with.</summary>
    private bool _asyncLobby;

    public event Action<LiveRoundCardDto>? RoundStarted;
    public event Action<LiveRoundRevealDto>? RoundRevealed;
    public event Action<LiveEndedDto>? Ended;
    public event Action<OpponentPresenceDto>? OpponentLeft;
    public event Action<OpponentPresenceDto>? OpponentBack;

    /// <summary>The #13 contract addition: fires once, on the first answer of a round, so the
    /// question phase can show "they have answered" without polling.</summary>
    public event Action<OpponentAnsweredDto>? OpponentAnswered;

    /// <summary>
    /// Issue #53's addition: fires once for whoever <c>CloseRound</c> just dropped to the miss
    /// streak. The eliminated player's own client is what switches to spectating on this; everyone
    /// else's is what stops waiting on a seat that will never fill again.
    /// </summary>
    public event Action<LivePlayerEliminatedDto>? PlayerEliminated;

    /// <summary>
    /// A rematch lobby now exists for this duel — pushed whether this connection's own press created
    /// it or somebody else's did; there is no separate "the other side wants a rematch" event any
    /// more (see <c>ILiveNotifier.RematchCreatedAsync</c>'s own remarks). Every other participant
    /// learns about it as an ordinary invitation instead, through <see cref="LobbyClient"/>.
    /// </summary>
    public event Action<RematchCreatedDto>? RematchCreated;

    /// <summary>The rematch lobby could not be created — the live kill switch is off.</summary>
    public event Action? RematchFailed;

    /// <summary>
    /// Issue #53's lobby page: the roster or the settings changed while the lobby is still open.
    /// Carries no payload — see <c>ILiveNotifier.LobbyUpdatedAsync</c>'s own remarks — so a listener
    /// re-fetches (<c>Api.JoinAsync</c>/<c>JoinLiveAsync</c>, both idempotent for someone already
    /// seated) rather than reading a pushed view straight off the event.
    /// </summary>
    public event Action? LobbyUpdated;

    /// <summary>
    /// Fires after a reconnect's automatic rejoin completes, with the fresh catch-up view. Whatever
    /// pushes were missed while the socket was down (a reveal, a new round, even the duel ending)
    /// arrive as ordinary <c>RoundStarted</c>/<c>RoundRevealed</c>/<c>Ended</c> events only from here
    /// on — this is what re-syncs the phase the client was in when it dropped.
    /// </summary>
    public event Action<LiveViewDto>? Rejoined;

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
        _connection.On<OpponentAnsweredDto>("OpponentAnswered", a => OpponentAnswered?.Invoke(a));
        _connection.On<LivePlayerEliminatedDto>("PlayerEliminated", e => PlayerEliminated?.Invoke(e));
        _connection.On<RematchCreatedDto>("RematchCreated", r => RematchCreated?.Invoke(r));
        _connection.On("RematchFailed", () => RematchFailed?.Invoke());
        _connection.On("LobbyUpdated", () => LobbyUpdated?.Invoke());
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
        _asyncLobby = false;
        var view = JoinInvokerOverrideForTests is { } overriddenJoin
            ? await overriddenJoin(matchId)
            : await _connection.InvokeAsync<LiveViewDto>("Join", matchId, ct);

        Skew = ComputeSkew(view.ServerNow, DateTimeOffset.UtcNow);
        return view;
    }

    /// <summary>
    /// Issue #53's lobby page: the async-lobby twin of <see cref="JoinAsync"/>, against
    /// <c>LiveHub.JoinAsyncLobby</c>. No catch-up view comes back over this hub method — an async
    /// duel's state is already fetched over plain REST (<c>GET /api/matches/{id}</c>, called via a
    /// join-by-code that is idempotent for someone already seated) — this only proves membership and
    /// joins the same per-match group so <see cref="LobbyUpdated"/> pushes arrive. False means the
    /// invoke could not be sent, the same convention every other bool-returning method here uses.
    /// </summary>
    public async Task<bool> JoinAsyncLobbyAsync(string matchId, CancellationToken ct = default)
    {
        _matchId = matchId;
        _asyncLobby = true;
        try
        {
            await _connection.InvokeAsync("JoinAsyncLobby", matchId, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Leaves whichever group this connection last joined — <see cref="JoinAsync"/>'s live
    /// duel or <see cref="JoinAsyncLobbyAsync"/>'s async lobby — and clears the state a reconnect
    /// would otherwise rejoin against.</summary>
    public Task LeaveAsync(CancellationToken ct = default)
    {
        var matchId = _matchId;
        var wasAsyncLobby = _asyncLobby;
        _matchId = null;
        _asyncLobby = false;
        if (matchId is null) return Task.CompletedTask;
        return _connection.InvokeAsync(wasAsyncLobby ? "LeaveAsyncLobby" : "Leave", matchId, ct);
    }

    /// <summary>
    /// <paramref name="response"/> is the answer a choice index cannot hold — served positions for a
    /// sorting question, a country code or <c>"lat,lon"</c> for a map one — and travels beside a
    /// <paramref name="choiceIndex"/> of -1. It is always sent, null included: a SignalR invocation
    /// has to match the hub method's arity, so omitting the argument for an ordinary choice answer
    /// would fail the call rather than default it.
    /// </summary>
    public Task AnswerAsync(string matchId, int round, int choiceIndex, string? response = null, CancellationToken ct = default)
        => _connection.InvokeAsync("Answer", matchId, round, choiceIndex, response, ct);

    public Task<RematchOutcomeDto> RematchAsync(string matchId, CancellationToken ct = default)
        => _connection.InvokeAsync<RematchOutcomeDto>("Rematch", matchId, ct);

    /// <summary>The reconnected handler: without this, a page that survives a drop would be stuck on
    /// stale state forever. An async lobby has no catch-up view to rejoin with — it simply rejoins the
    /// group, and the lobby page's own state is whatever it last fetched over REST until the next
    /// <see cref="LobbyUpdated"/> push (or a manual refresh) brings it current.</summary>
    internal Task OnReconnectedAsync(string? connectionId) => _matchId is not { } id
        ? Task.CompletedTask
        : _asyncLobby ? _connection.InvokeAsync("JoinAsyncLobby", id) : RejoinAsync(id);

    private async Task RejoinAsync(string matchId)
    {
        var view = await JoinAsync(matchId);
        Rejoined?.Invoke(view);
    }

    /// <summary>Pure function so it is testable with no connection at all: server time minus local time.</summary>
    public static TimeSpan ComputeSkew(DateTimeOffset serverNow, DateTimeOffset localNow) => serverNow - localNow;

    public async ValueTask DisposeAsync()
    {
        _connection.Reconnected -= OnReconnectedAsync;
        await _connection.DisposeAsync();
    }
}
