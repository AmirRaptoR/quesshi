namespace Quesshi.Application.Sessions;

/// <summary>
/// Observes completed live-session turns. The production implementation is a no-op;
/// benchmark hosts can supply an in-process observer without changing game behaviour.
/// </summary>
public interface ISessionTurnObserver
{
    void Record(SessionTurnSample sample);

    void RecordTimerLag(TimeSpan lag);

    /// <summary>Time from an ordinary answer being accepted in memory to its batch committing.</summary>
    void RecordAnswerCommit(TimeSpan elapsed) { }
}

public enum SessionTurnKind
{
    ColdActivation,
    OrdinaryAnswer,
    RoundClose,
    Finish
}

public enum SessionTurnSource
{
    Command,
    Timer,
}

internal static class SessionTurnClassifier
{
    public static SessionTurnKind? ForCommand(
        bool activated,
        bool changed,
        bool isAnswer,
        int? previousPhase,
        int? currentPhase)
    {
        if (activated)
        {
            return SessionTurnKind.ColdActivation;
        }

        if (!changed)
        {
            return null;
        }

        if (!isAnswer)
        {
            return null;
        }

        return previousPhase == 1 && currentPhase == 2
            ? SessionTurnKind.RoundClose
            : SessionTurnKind.OrdinaryAnswer;
    }

    public static SessionTurnKind? ForTimeout(
        bool changed,
        int? previousPhase,
        int? currentPhase,
        bool sessionCompleted)
    {
        if (!changed)
        {
            return null;
        }

        if (previousPhase == 1 && currentPhase == 2)
        {
            return SessionTurnKind.RoundClose;
        }

        return sessionCompleted ? SessionTurnKind.Finish : null;
    }
}

public sealed record SessionTurnSample(
    SessionTurnKind Kind,
    TimeSpan Duration,
    DateTimeOffset? DueAt,
    DateTimeOffset CompletedAt,
    SessionTurnSource Source = SessionTurnSource.Command);

public sealed class NullSessionTurnObserver : ISessionTurnObserver
{
    public static readonly NullSessionTurnObserver Instance = new();

    private NullSessionTurnObserver()
    {
    }

    public void Record(SessionTurnSample sample)
    {
    }

    public void RecordTimerLag(TimeSpan lag)
    {
    }
}
