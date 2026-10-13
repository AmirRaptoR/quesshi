namespace Quesshi.Application.Sessions;

/// <summary>Command-path boundary for reconciling a session deadline before a format is invoked.</summary>
public interface ISessionDeadlineReconciler
{
    Task ReconcileAsync(SessionState state, CancellationToken cancellationToken = default);
}

/// <summary>Timer delivery is introduced separately; commands still cross this boundary today.</summary>
public sealed class NoopSessionDeadlineReconciler : ISessionDeadlineReconciler
{
    public Task ReconcileAsync(SessionState state, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
