namespace Quesshi.Web.Services;

/// <summary>The one cue (if any) a finished duel's outcome earns.</summary>
public enum SoundOutcome { Win, Loss, None }

/// <summary>
/// How the lowercase "win"/"draw"/"loss" outcome convention every DTO on this API already uses (see
/// <c>StandingRowDto</c> and <c>MatchSummaryDto</c>'s own remarks) becomes a <see cref="SoundOutcome"/>
/// — pulled out the same way <c>LiveOutcomeText</c> and <c>StandingsPresentation</c> keep their own
/// mappings testable without a browser. A draw earns neither cue: declared a tie is not quite a win,
/// and playing the loss buzz for it would be worse than playing nothing. The same "not resolved yet"
/// reasoning covers <c>MatchSummaryDto.Outcome</c>'s fourth value, <c>"pending"</c> — an async run that
/// finished before the opponent's has nothing to announce until the duel itself is over.
/// </summary>
public static class SoundOutcomeMapping
{
    public static SoundOutcome ForOutcome(string outcome) => outcome switch
    {
        "win" => SoundOutcome.Win,
        "loss" => SoundOutcome.Loss,
        _ => SoundOutcome.None // "draw", "pending", or anything else not yet resolved
    };
}
