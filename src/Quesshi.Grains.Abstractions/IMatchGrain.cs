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
    [Alias("AnswerAsync")]
    Task<AnswerOutcome> AnswerAsync(string playerId, int slot, int choiceIndex);

    /// <summary>Redacted for the asking player: the opponent's choices appear only once you have finished your own run.</summary>
    [Alias("GetAsync")]
    Task<MatchView?> GetAsync(string forPlayerId);
}
