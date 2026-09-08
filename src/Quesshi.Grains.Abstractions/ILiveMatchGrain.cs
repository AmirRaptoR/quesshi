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
    /// The owner starts the duel once at least two are seated — the affordance a lobby with room to
    /// spare needs, since reaching capacity already starts it on its own. Refused for anyone but the
    /// owner, below two participants, or once the lobby has already left the lobby phase.
    /// </summary>
    [Alias("StartAsync")]
    Task<bool> StartAsync(string playerId);

    /// <summary>
    /// A seated player leaves before the duel starts. For anyone but the owner this frees their seat;
    /// for the owner it ends the whole lobby as a no-contest instead, since there is no ownership
    /// transfer. Idempotent-refusing rather than idempotent: a second call for someone already gone
    /// (or a call once the duel has started) returns false and changes nothing.
    /// </summary>
    [Alias("LeaveAsync")]
    Task<bool> LeaveAsync(string playerId);

    /// <summary>
    /// The owner changes the lobby's <c>DuelSettings</c>. Refused for anyone but the owner, and
    /// refused once the question set is drawn — see <see cref="CreateLobbyAsync"/>'s own remarks on why
    /// that crosses the boundary as primitives.
    /// </summary>
    [Alias("UpdateSettingsAsync")]
    Task<bool> UpdateSettingsAsync(string playerId, int lang, int questionCount, List<string> categoryIds, List<int> levels);

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
    /// answered, not a participant, duel over). No outcome comes back: the opponent is still on the
    /// same question, so returning the correct index here would leak the reveal to whoever answers
    /// first. Both players learn it together, through <c>ILiveNotifier.RoundRevealedAsync</c>.
    /// </summary>
    [Alias("AnswerAsync")]
    Task<bool> AnswerAsync(string playerId, int slot, int choiceIndex);

    /// <summary>Redacted for the asking player: the round in flight never reveals the correct index or the opponent's choice.</summary>
    [Alias("GetAsync")]
    Task<LiveView?> GetAsync(string forPlayerId);

    /// <summary>Admin-facing: finishes an in-flight duel early as a no-contest.</summary>
    [Alias("EndAsync")]
    Task EndAsync(string reason);

    /// <summary>
    /// A participant of a <em>finished</em> duel marks itself ready for a rematch. Symmetric and
    /// idempotent: the first press records readiness and waits, a second press by the same player
    /// changes nothing, and only a press by the <em>other</em> participant — while this one's
    /// readiness has not expired — creates the fresh duel. The caller is refused outright (no
    /// readiness recorded) when it is not a participant, the duel is not over, or it never got an
    /// opponent at all.
    /// </summary>
    [Alias("RequestRematchAsync")]
    Task<RematchOutcome> RequestRematchAsync(string playerId);
}
