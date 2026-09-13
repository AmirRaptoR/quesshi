namespace Quesshi.Domain;

/// <summary>The three ways a participant can complete a matching slot.</summary>
public enum MatchingAnswerKind
{
    SelectedParticipant = 0,
    SelectedChoice = 1,
    NotApplicable = 2
}
