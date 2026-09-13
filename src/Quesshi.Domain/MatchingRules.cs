namespace Quesshi.Domain;

/// <summary>The bounds on a <see cref="MatchingQuestion"/>'s <see cref="MatchingAnswerSource.Fixed"/>
/// choice list, so no algorithm hardcodes them.</summary>
public static class MatchingRules
{
    public const int MinFixedChoices = 2;

    /// <summary>Derived from <see cref="MatchRules.MaxParticipants"/> rather than a separate literal,
    /// so a served question's fixed choices can never outnumber the participants that could be
    /// matched against them.</summary>
    public const int MaxFixedChoices = MatchRules.MaxParticipants;

    /// <summary>
    /// How long a participant may leave the current matching slot unanswered before the next
    /// clock advance marks them inactive. Matching has no question timer; this is an activity
    /// rule used only to prevent one abandoned participant from blocking the answer barrier.
    /// </summary>
    public static readonly TimeSpan IdleAfter = MatchRules.ForfeitAfter;
}
