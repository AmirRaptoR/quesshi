using Quesshi.Application.Sessions;

namespace Quesshi.Application.Tests;

public sealed class SessionTurnClassifierTests
{
    [Fact]
    public void Closing_answers_timeouts_and_unchanged_commands_are_classified_for_measurement()
    {
        Assert.Equal(
            SessionTurnKind.RoundClose,
            SessionTurnClassifier.ForCommand(false, true, true, previousPhase: 1, currentPhase: 2));
        Assert.Equal(
            SessionTurnKind.OrdinaryAnswer,
            SessionTurnClassifier.ForCommand(false, true, true, previousPhase: 1, currentPhase: 1));
        Assert.Equal(
            SessionTurnKind.RoundClose,
            SessionTurnClassifier.ForTimeout(true, previousPhase: 1, currentPhase: 2, sessionCompleted: false));
        Assert.Equal(
            SessionTurnKind.Finish,
            SessionTurnClassifier.ForTimeout(true, previousPhase: 2, currentPhase: 0, sessionCompleted: true));
        Assert.Null(SessionTurnClassifier.ForCommand(false, false, true, previousPhase: 1, currentPhase: 1));
        Assert.Null(SessionTurnClassifier.ForTimeout(false, previousPhase: 1, currentPhase: 2, sessionCompleted: false));
    }
}
