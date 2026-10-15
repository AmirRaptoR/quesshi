namespace Quesshi.Application.Sessions;

/// <summary>
/// Observes completed live-session turns. The production implementation is a no-op;
/// benchmark hosts can supply an in-process observer without changing game behaviour.
/// </summary>
public interface ISessionTurnObserver
{
    void Record(SessionTurnSample sample);

    void RecordTimerLag(TimeSpan lag);
}

public enum SessionTurnKind
{
    OrdinaryAnswer,
    RoundClose,
    Finish
}

public sealed record SessionTurnSample(
    SessionTurnKind Kind,
    TimeSpan Duration,
    DateTimeOffset? DueAt,
    DateTimeOffset CompletedAt);

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
