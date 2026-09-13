namespace Quesshi.Domain;

/// <summary>
/// Where a <see cref="MatchingQuestion"/>'s options come from. <see cref="Participants"/> is the
/// common case — the choices are substituted in from the match's own players when the question is
/// served, so the aggregate stores none. <see cref="Fixed"/> is an authored list of 2 to
/// <see cref="MatchingRules.MaxFixedChoices"/> choices with no correct one — matching has no answer
/// to grade, only pairs to compare.
/// </summary>
public enum MatchingAnswerSource { Participants = 0, Fixed = 1 }
