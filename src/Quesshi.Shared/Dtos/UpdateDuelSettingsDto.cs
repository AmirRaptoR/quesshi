namespace Quesshi.Shared;

/// <summary>
/// The owner replacing a lobby's <c>DuelSettings</c> wholesale — not a partial patch, since the owner's
/// settings form always submits the whole thing it is showing. Refused server-side once the question
/// set is drawn (<c>ILiveMatchGrain.UpdateSettingsAsync</c>'s own remarks), for anyone but the owner,
/// and for a count outside <c>MatchRules.QuestionCountChoices</c> — the last of which is coerced to the
/// default rather than rejected, the same courtesy <c>CreateMatchDto</c>/<c>CreateLobbyDto</c> already
/// get at creation.
/// <para>
/// <see cref="Capacity"/> is genuinely optional, unlike the settings fields above: null means "leave
/// capacity alone", not "reset to a default" — the seat stepper sends it alongside the lobby's
/// <em>current</em> settings, so a capacity-only change never resets question count, language,
/// categories or levels. Validated 2-8 at the endpoint before the domain ever sees it, same as
/// <c>CreateLobbyDto.Capacity</c>.
/// </para>
/// </summary>
public sealed record UpdateDuelSettingsDto(string? Lang = null,
    List<string>? Categories = null, int? Questions = null, List<int>? Levels = null, int? Capacity = null);
