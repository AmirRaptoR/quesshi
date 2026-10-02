namespace Quesshi.Domain;

/// <summary>The ways a participant can complete a voting slot.</summary>
public enum VotingAnswerKind
{
    SelectedParticipant = 0,
    SelectedChoice = 1,
    // Retained for persisted matches served before the participant fallbacks were split.
    NotApplicable = 2,
    MultipleParticipants = 3,
    NoParticipant = 4
}
