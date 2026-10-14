using Dapper;
using Orleans;
using Quesshi.Engine.Contracts;
using Quesshi.Grains.Abstractions;
using Quesshi.Infrastructure.Database;
using Quesshi.Server.Health;

namespace Quesshi.Server.Sessions;

/// <summary>One overdue session returned by the system-only discovery function.</summary>
internal sealed record DueSession(Guid TenantId, Guid SessionId, DateTimeOffset DueAt);

internal interface IDueSessionSource
{
    Task<IReadOnlyList<DueSession>> FindAsync(DateTimeOffset asOf, int maxRows, IReadOnlyCollection<Guid> skip, CancellationToken cancellationToken);
}

/// <summary>Reads only the identifiers that a silo may dispatch; session writes remain in the grain.</summary>
internal sealed class PostgresDueSessionSource(DatabaseSources sources) : IDueSessionSource
{
    public async Task<IReadOnlyList<DueSession>> FindAsync(DateTimeOffset asOf, int maxRows, IReadOnlyCollection<Guid> skip, CancellationToken cancellationToken)
    {
        await using var connection = await sources.System.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<DueSession>(new CommandDefinition("""
            select tenant_id as TenantId, id as SessionId, due_at as DueAt
            from due_sessions(@asOf, @maxRows, @skip)
            """, new { asOf, maxRows, skip = skip.ToArray() }, cancellationToken: cancellationToken));
        return rows.AsList();
    }
}

internal interface ISessionTimeoutDispatcher
{
    Task DispatchAsync(DueSession session);
}

internal sealed class OrleansSessionTimeoutDispatcher(IGrainFactory grains) : ISessionTimeoutDispatcher
{
    public Task DispatchAsync(DueSession session) =>
        grains.GetGrain<ISessionGrain>($"{session.TenantId}/{session.SessionId}").TimeoutAsync(session.DueAt);
}

/// <summary>
/// Dispatches overdue sessions without waiting for an individual grain call. Failed calls are kept
/// out of this silo's next discovery passes only; no session row is modified here.
/// </summary>
internal sealed class DeadlineSweep(
    IDueSessionSource sessions,
    ISessionTimeoutDispatcher dispatcher,
    IClock clock,
    ILogger<DeadlineSweep> logger)
{
    internal const int MaximumInFlight = 256;
    private readonly Dictionary<Guid, InFlight> _inFlight = [];
    private readonly Dictionary<Guid, HeldCall> _held = [];

    public async Task RunPassAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.Now;
        await ReapCompletedAsync(now, cancellationToken);
        foreach (var sessionId in _held.Where(pair => pair.Value.RetryAt <= now).Select(pair => pair.Key).ToArray())
            _held.Remove(sessionId);

        var skip = _inFlight.Keys.Concat(_held.Keys).ToArray();
        var remaining = MaximumInFlight - _inFlight.Count;
        var due = await sessions.FindAsync(now, Math.Max(0, remaining), skip, cancellationToken);
        foreach (var session in due)
        {
            if (_inFlight.Count >= MaximumInFlight) break;
            if (_inFlight.ContainsKey(session.SessionId) || _held.ContainsKey(session.SessionId)) continue;
            _inFlight.Add(session.SessionId, new(session, dispatcher.DispatchAsync(session)));
        }
    }

    private async Task ReapCompletedAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var (sessionId, call) in _inFlight.Where(pair => pair.Value.Task.IsCompleted).ToArray())
        {
            _inFlight.Remove(sessionId);
            try
            {
                await call.Task;
                _held.Remove(sessionId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var attempts = _held.TryGetValue(sessionId, out var prior) ? prior.Attempts + 1 : 1;
                var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempts - 1)));
                _held[sessionId] = new(attempts, now.Add(delay));
                logger.LogWarning(exception, "Deadline delivery failed for {SessionId}; holding it until {RetryAt}", sessionId, now.Add(delay));
            }
        }
    }

    private sealed record InFlight(DueSession Session, Task Task);
    private sealed record HeldCall(int Attempts, DateTimeOffset RetryAt);
}

/// <summary>Runs the system discovery query every second and publishes its last successful pass to health.</summary>
internal sealed class DeadlineSweepService(
    DeadlineSweep sweep,
    IBackgroundLoopRegistry loops,
    ILogger<DeadlineSweepService> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    internal const string LoopName = "deadline-sweep";
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private readonly IBackgroundLoop _loop = loops.Register(LoopName);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval, _timeProvider);
        try
        {
            do
            {
                try
                {
                    await sweep.RunPassAsync(stoppingToken);
                    _loop.Passed();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Deadline sweep pass failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
