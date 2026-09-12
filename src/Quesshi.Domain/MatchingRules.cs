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
}
