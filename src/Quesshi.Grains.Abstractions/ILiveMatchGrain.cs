namespace Quesshi.Grains.Abstractions;

[Alias("Quesshi.Grains.Abstractions.ILiveMatchGrain")]
public interface ILiveMatchGrain : IGrainWithStringKey
{
    /// <summary>Idempotent: returns the existing view if the duel already exists.</summary>
    [Alias("CreateAsync")]
    Task<LiveView> CreateAsync(string code, int lang, string challengerId, List<string> questionIds);

    /// <summary>
    /// The lobby-aware create path: opens a lobby for <paramref name="ownerId"/> with these settings
    /// and this <paramref name="capacity"/> (2-8), with no question set drawn yet — <c>StartAsync</c>
    /// draws it from whatever the settings say at that instant. Idempotent, exactly like
    /// <see cref="CreateAsync"/>. <c>DuelSettings</c> crosses this boundary as the same
    /// primitives <paramref name="lang"/> already is one of, and <paramref name="levels"/> as
    /// <c>Difficulty</c> ordinals, because <c>Quesshi.Grains.Abstractions</c> deliberately never
    /// references <c>Quesshi.Domain</c>.
    /// </summary>
    [Alias("CreateLobbyAsync")]
    Task<LiveView> CreateLobbyAsync(string code, string ownerId, int lang, int questionCount,
        List<string> categoryIds, List<int> levels, int capacity);

    /// <summary>
    /// A <see cref="LiveJoinResult"/> carried as <c>int</c>: <see cref="Quesshi.Grains.Abstractions"/>
    /// keeps its Orleans-SDK-only reference list, so the domain enum crosses the boundary as a
    /// number rather than a type this project would have to reference <c>Quesshi.Domain</c> for.
    /// </summary>
    [Alias("JoinAsync")]
    Task<int> JoinAsync(string playerId);

    /// <summary>
    /// The owner starts the duel once at least two are seated — the only way a lobby ever leaves the
    /// lobby phase, whether it is full or has room to spare. Refused for anyone but the owner, below
    /// two participants, or once the lobby has already left the lobby phase.
    /// </summary>
    [Alias("StartAsync")]
    Task<bool> StartAsync(string playerId);

    /// <summary>
    /// The random-matchmaking counterpart to <see cref="StartAsync"/>: no owner check, since the
    /// joiner who just got paired calls this, not the lobby's owner. Called from exactly the two
    /// pairing sites — <c>LiveMatchmakingGrain.BuildDuelAsync</c> and the live random queue — right
    /// after both sides are seated, now that <see cref="JoinAsync"/> no longer starts a duel on its
    /// own once it fills every seat.
    /// </summary>
    [Alias("StartPairedAsync")]
    Task<bool> StartPairedAsync();

    /// <summary>
    /// A seated player leaves before the duel starts. For anyone but the owner this frees their seat;
    /// for the owner it ends the whole lobby as a no-contest instead, since there is no ownership
    /// transfer. Idempotent-refusing rather than idempotent: a second call for someone already gone
    /// (or a call once the duel has started) returns false and changes nothing.
    /// </summary>
    [Alias("LeaveAsync")]
    Task<bool> LeaveAsync(string playerId);

    /// <summary>
    /// The owner changes the lobby's <c>DuelSettings</c> and, optionally, its <c>Capacity</c> in the
    /// same call — atomically: both halves are validated before either is applied, so a refused
    /// capacity change never leaves the settings half applied on its own, or vice versa.
    /// <paramref name="capacity"/> null means "leave capacity alone". Refused for anyone but the
    /// owner; the settings half is refused once the question set is drawn, and the capacity half
    /// below 2, above 8, below the seated count, or once the lobby has left the lobby phase — see
    /// <see cref="CreateLobbyAsync"/>'s own remarks on why <c>DuelSettings</c> crosses this boundary as
    /// primitives.
    /// </summary>
    [Alias("UpdateSettingsAsync")]
    Task<bool> UpdateSettingsAsync(string playerId, int lang, int questionCount, List<string> categoryIds, List<int> levels, int? capacity);

    /// <summary>
    /// True only when the caller is the owner and the duel is still in the lobby: it ends the
    /// duel as a no-contest and notifies. False leaves the duel running untouched. Predates
    /// <see cref="LeaveAsync"/>, which every new caller should prefer — this stays for the callers
    /// that already depend on its owner-only refusal.
    /// </summary>
    [Alias("CancelAsync")]
    Task<bool> CancelAsync(string playerId);

    /// <summary>
    /// True if the answer was recorded, false if it was refused (late, wrong slot, already
    /// answered, not a participant, duel over, or a malformed sorting or map response). No outcome
    /// comes back: the opponent is still on the same question, so returning the correct index here
    /// would leak the reveal to whoever answers first. Both players learn it together, through
    /// <c>ILiveNotifier.RoundRevealedAsync</c>.
    /// <para>
    /// <paramref name="response"/> is the answer <paramref name="choiceIndex"/> cannot hold, and the
    /// two are alternatives rather than companions: a choice question travels as an index with a null
    /// response, a sorting question as served positions in the order the player placed them
    /// (<c>"2,0,3,1"</c> — "the item you showed me third goes first") with the index at -1, and a map
    /// question as a country code or <c>"lat,lon"</c>, likewise at -1. The grain, which is the only
    /// place that can reconstruct the round's shuffle, normalises a sorting answer into stored-index
    /// terms before storing it.
    /// </para>
    /// </summary>
    [Alias("AnswerAsync")]
    Task<bool> AnswerAsync(string playerId, int slot, int choiceIndex, string? response = null);

    /// <summary>Redacted for the asking player: the round in flight never reveals the correct index or the opponent's choice.</summary>
    [Alias("GetAsync")]
    Task<LiveView?> GetAsync(string forPlayerId);

    /// <summary>
    /// A no-side-effect read for a page loading `/lobby/{code}` — nothing is joined, started, drawn
    /// or written. Returns the view when <paramref name="forPlayerId"/> is already a participant (at
    /// any phase), or when the duel is still in its lobby phase (for an unseated visitor deciding
    /// whether to take a seat), and null otherwise. Deliberately not <see cref="GetAsync"/>: that
    /// method's null-for-non-participant return is <c>LiveHub.Join</c>'s own proof of participation,
    /// and relaxing it here instead would open the gameplay hub to anyone who merely knows a code.
    /// </summary>
    [Alias("LobbyViewAsync")]
    Task<LiveView?> LobbyViewAsync(string forPlayerId);

    /// <summary>Admin-facing: finishes an in-flight duel early as a no-contest.</summary>
    [Alias("EndAsync")]
    Task EndAsync(string reason);

    /// <summary>
    /// A participant of a <em>finished</em> duel asks for a rematch: creates a lobby with this duel's
    /// own settings and capacity, and auto-invites every other participant — whoever turns up, plays.
    /// Idempotent about the lobby itself, not just about readiness: the lobby's id is derived from this
    /// duel's id (see the implementation's own remarks), so however many participants call this,
    /// however many times, they all land on the same lobby with no reference stored anywhere and
    /// nothing to orphan. Refused outright when the caller is not a participant, the duel is not over,
    /// or it never got past a single seat.
    /// </summary>
    [Alias("RequestRematchAsync")]
    Task<RematchOutcome> RequestRematchAsync(string playerId);
}
