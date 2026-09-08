namespace Quesshi.Shared;

/// <summary>
/// The wire shape of <c>Quesshi.Domain.DuelSettings</c> — issue #53's lobby page needs to show what
/// the owner picked (and, for the owner, let them change it) before any question is drawn, which is
/// exactly what neither <c>LiveViewDto</c> nor <c>MatchSummaryDto</c> carried before this: both
/// already had a <c>Questions</c>/<c>TotalRounds</c> count, but that count is the *drawn* set's size —
/// zero for a lobby nobody has started — never the owner's current pick. This is that pick, always
/// present regardless of whether the draw has happened yet. <see cref="Lang"/> is the same lower-case
/// code every other language field on this API uses ("fa"/"en"/"nl").
/// </summary>
public sealed record DuelSettingsDto(string Lang, int QuestionCount, List<string> CategoryIds, List<int> Levels);
