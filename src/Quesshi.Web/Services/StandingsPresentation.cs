namespace Quesshi.Web.Services;

/// <summary>
/// How a <c>StandingRowDto.Outcome</c> string becomes a chip class and a translation key — pulled out
/// of <c>StandingsList.razor</c>'s own markup, the same way <c>LiveOutcomeText</c> and
/// <c>LivePhaseSelector</c> keep their own mappings testable without bUnit. The <c>Expired</c> flag is
/// deliberately not consulted here: an unfinished async run still has a real, ranked outcome once the
/// match is over (see <c>StandingRowDto</c>'s own remarks) — expiry is shown alongside the outcome
/// chip, not instead of it.
/// </summary>
public static class StandingsPresentation
{
    public static string ChipClass(string outcome) => outcome switch
    {
        "win" => "chip--win",
        "draw" => "chip--draw",
        _ => "chip--loss"
    };

    public static string OutcomeKey(string outcome) => outcome switch
    {
        "win" => "result.outcome.win",
        "draw" => "result.outcome.draw",
        _ => "result.outcome.loss"
    };
}
