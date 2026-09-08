namespace Quesshi.Web.Services;

/// <summary>The five bodies <c>Live.razor</c> switches between. One extra name over the wire's
/// four visible phases plus "over": <c>Ended</c> reads better in markup than <c>Over</c>.</summary>
public enum LiveBody { Lobby, Countdown, Question, Reveal, Ended }

/// <summary>
/// Maps the wire's lower-cased phase word to the body a client renders. Kept out of the page's
/// markup so the mapping is unit-tested without bUnit — see #13's "phase-to-body selection" test
/// criterion.
/// </summary>
public static class LivePhaseSelector
{
    public static LiveBody Select(string phase) => phase.ToLowerInvariant() switch
    {
        "lobby" => LiveBody.Lobby,
        "countdown" => LiveBody.Countdown,
        "question" => LiveBody.Question,
        "reveal" => LiveBody.Reveal,
        "over" => LiveBody.Ended,
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "unknown live phase")
    };
}
