namespace Quesshi.Domain;

/// <summary>The ways a participant can complete a matching slot.</summary>
public enum MatchingAnswerKind
{
    SelectedParticipant = 0,
    SelectedChoice = 1,
    // Retained for persisted matches served before the participant fallbacks were split.
    NotApplicable = 2,
    MultipleParticipants = 3,
    NoParticipant = 4
}
