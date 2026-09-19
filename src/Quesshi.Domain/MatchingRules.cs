namespace Quesshi.Domain;

/// <summary>The bounds on a <see cref="MatchingQuestion"/>'s <see cref="MatchingAnswerSource.Fixed"/>
/// choice list, so no algorithm hardcodes them.</summary>
public static class MatchingRules
{
    public const int MinFixedChoices = 2;
    public const int MaxParticipants = 8;

    /// <summary>Matching remains an eight-seat bounded context even when trivia lobbies grow.</summary>
    public const int MaxFixedChoices = 8;

    /// <summary>
    /// How long a participant may leave the current matching slot unanswered before the next
    /// clock advance marks them inactive. Matching has no question timer; this is an activity
    /// rule used only to prevent one abandoned participant from blocking the answer barrier.
    /// </summary>
    public static readonly TimeSpan IdleAfter = MatchRules.ForfeitAfter;
}
