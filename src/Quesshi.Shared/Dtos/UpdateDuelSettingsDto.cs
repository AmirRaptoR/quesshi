namespace Quesshi.Shared;

/// <summary>
/// The owner replacing a lobby's <c>DuelSettings</c> wholesale — not a partial patch, since the owner's
/// settings form always submits the whole thing it is showing. Refused server-side once the question
/// set is drawn (<c>ILiveMatchGrain.UpdateSettingsAsync</c>'s own remarks), for anyone but the owner,
/// and for a count outside <c>MatchRules.QuestionCountChoices</c> — the last of which is coerced to the
/// default rather than rejected, the same courtesy <c>CreateMatchDto</c>/<c>CreateLobbyDto</c> already
/// get at creation.
/// </summary>
public sealed record UpdateDuelSettingsDto(string? Lang = null,
    List<string>? Categories = null, int? Questions = null, List<int>? Levels = null);
