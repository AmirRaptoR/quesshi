using Quesshi.Formats.Live;

namespace Quesshi.Web.Plugins.Formats.Live;

/// <summary>The bodies <c>LiveScreen.razor</c> switches between; <c>Ended</c> covers a session that has stopped.</summary>
public enum LiveBody { Lobby, Countdown, Question, Reveal, Ended }

/// <summary>
/// Maps the session view's phase word to the body a client renders. The server writes a running live
/// session's phase as the format's phase number, so both that and the word form are accepted. Kept
/// out of the markup so the mapping is unit-tested without bUnit.
/// </summary>
public static class LivePhaseSelector
{
    public static LiveBody Select(string phase) => phase.ToLowerInvariant() switch
    {
        "idle" or "lobby" => LiveBody.Lobby,
        "0" or "countdown" => LiveBody.Countdown,
        "1" or "question" => LiveBody.Question,
        "2" or "reveal" => LiveBody.Reveal,
        "over" => LiveBody.Ended,
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "unknown live phase")
    };

    /// <summary>The wire word of a format phase number.</summary>
    public static string WordFor(int phase) => phase switch
    {
        LiveFormat.CountdownPhase => "countdown",
        LiveFormat.QuestionPhase => "question",
        LiveFormat.RevealPhase => "reveal",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "unknown live phase")
    };
}
