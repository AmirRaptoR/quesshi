namespace Quesshi.Web.Services;

/// <summary>
/// How a finished live duel ended, from the viewer's side. Distinct from <c>MatchState</c>: a
/// resolved duel splits into won/lost/drawn, and an abandoned one into who went quiet, because each
/// needs its own headline (see #13). The client computes this from whatever the eventual
/// <c>LiveView</c> contract (#10/#11) carries; it takes no dependency on that contract itself.
/// </summary>
public enum LiveOutcome { Won, Lost, Draw, AbandonedByThem, AbandonedByYou, NoContest }

/// <summary>Six ways a live duel can end, six distinct translated headlines.</summary>
public static class LiveOutcomeText
{
    public static string HeadlineKey(LiveOutcome outcome) => outcome switch
    {
        LiveOutcome.Won => "result.win",
        LiveOutcome.Lost => "result.loss",
        LiveOutcome.Draw => "result.draw",
        LiveOutcome.AbandonedByThem => "live.end.abandonedByThem",
        LiveOutcome.AbandonedByYou => "live.end.abandonedByYou",
        LiveOutcome.NoContest => "live.end.noContest",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
    };
}
