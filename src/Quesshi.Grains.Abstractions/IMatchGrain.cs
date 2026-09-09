namespace Quesshi.Grains.Abstractions;

[Alias("Quesshi.Grains.Abstractions.IMatchGrain")]
public interface IMatchGrain : IGrainWithStringKey
{
    [Alias("CreateAsync")]
    Task<MatchView> CreateAsync(int lang, string challengerId, List<string> questionIds, string code);

    /// <summary>
    /// The lobby-aware create path: opens a lobby for <paramref name="ownerId"/> with these settings
    /// and this <paramref name="capacity"/> (2-8), with no question set drawn yet — <c>StartAsync</c>
    /// draws it from whatever the settings say at that instant. Idempotent, exactly like
    /// <see cref="CreateAsync"/>. <c>DuelSettings</c> crosses this boundary as the same primitives
    /// <paramref name="lang"/> already is one of, and <paramref name="levels"/> as <c>Difficulty</c>
    /// ordinals, because <c>Quesshi.Grains.Abstractions</c> deliberately never references
    /// <c>Quesshi.Domain</c>.
    /// </summary>
    [Alias("CreateLobbyAsync")]
    Task<MatchView> CreateLobbyAsync(string code, string ownerId, int lang, int questionCount,
        List<string> categoryIds, List<int> levels, int capacity);

    [Alias("JoinAsync")]
    Task<bool> JoinAsync(string playerId);

    /// <summary>
    /// The owner starts the duel once at least two are seated — the affordance a lobby with room to
    /// spare needs, since reaching capacity already starts it on its own. Refused for anyone but the
    /// owner, below two participants, or once joins have already closed.
    /// </summary>
    [Alias("StartAsync")]
    Task<bool> StartAsync(string playerId);

    /// <summary>
    /// A seated player leaves before the duel starts. For anyone but the owner this frees their seat;
    /// for the owner it ends the whole lobby as a no-contest instead, since there is no ownership
    /// transfer. Refuses (returns false, changes nothing) once the duel has started, or for a caller
    /// who is not seated.
    /// </summary>
    [Alias("LeaveAsync")]
    Task<bool> LeaveAsync(string playerId);

    /// <summary>
    /// The owner changes the lobby's <c>DuelSettings</c>. Refused for anyone but the owner, and
    /// refused once the question set is drawn.
    /// </summary>
    [Alias("UpdateSettingsAsync")]
    Task<bool> UpdateSettingsAsync(string playerId, int lang, int questionCount, List<string> categoryIds, List<int> levels);

    [Alias("ServeNextAsync")]
    Task<ServedSlot?> ServeNextAsync(string playerId);
    /// <summary>
    /// Records one answer of a run and says how it went. <paramref name="response"/> is the answer
    /// <paramref name="choiceIndex"/> cannot hold — served positions for a sorting question
    /// (<c>"2,0,3,1"</c>, meaning "the item you showed me third goes first"), a country code or
    /// <c>"lat,lon"</c> for a map one — and the index is -1 for both, exactly as it is for a timeout.
    /// The two are told apart by the response, never by the index: a timed-out sorting or map answer
    /// is -1 with <paramref name="response"/> null, a played one is -1 with a response.
    /// <para>
    /// Throws <see cref="InvalidOperationException"/> with <c>bad_response</c> for a submission no
    /// interface could have produced — a sorting order that is not a permutation of the items, a map
    /// answer that does not parse — and records nothing, the same refusal a bad choice index gets.
    /// </para>
    /// </summary>
    [Alias("AnswerAsync")]
    Task<AnswerOutcome> AnswerAsync(string playerId, int slot, int choiceIndex, string? response = null);

    /// <summary>Redacted for the asking player: the opponent's choices appear only once you have finished your own run.</summary>
    [Alias("GetAsync")]
    Task<MatchView?> GetAsync(string forPlayerId);
}
