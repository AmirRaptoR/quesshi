using System.Text.Json;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using Quesshi.Application.Sessions;
using Quesshi.Engine.Contracts;
using Quesshi.Grains.Abstractions;
using Quesshi.Plugin.Api;
using Quesshi.Shared;

namespace Quesshi.Grains;

/// <summary>
/// The sole in-memory writer for one tenant session. The grain is reentrant only so a command waiting for its
/// answer batch to commit does not hold up the next one; every state change and write runs inside <c>_gate</c>
/// (architecture 7.4.1).
/// </summary>
[Reentrant]
public class SessionGrain(
    ISessionSnapshotLoader snapshots,
    SessionStateStore store,
    IFormatRegistry formats,
    IKindRegistry kinds,
    IClock clock,
    IViewRenderer views,
    ISessionDeadlineReconciler deadlines,
    ILogger<SessionGrain> logger,
    ISessionTurnObserver? turnObserver = null,
    ISessionLobby? lobby = null) : Grain, ISessionGrain
{
    private readonly ISessionTurnObserver _turnObserver = turnObserver ?? NullSessionTurnObserver.Instance;
    private SessionState? _state;
    private Guid _tenantId;
    private Guid _sessionId;
    private IGrainTimer? _deadlineTimer;
    private DateTimeOffset? _timerWakeAt;
    private DateTimeOffset? _timerDueAt;
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Set while deferred answers are uncommitted: the durable state they were applied on top of.
    private SessionState? _baseline;
    // Answers accepted in memory that no write has picked up yet, and the batch that is being written.
    private List<PendingAnswer> _pending = [];
    private List<PendingAnswer> _inflight = [];
    // Non-null while a flush loop runs; completes when it has stopped. _exclusive asks it to stop after its current batch.
    private TaskCompletionSource? _flushIdle;
    private bool _exclusive;
    private int _generation;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var parts = this.GetPrimaryKeyString().Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out _tenantId) || !Guid.TryParse(parts[1], out _sessionId))
            throw new ArgumentException("Session grain keys must be '{tenantId}/{sessionId}'.");
        return base.OnActivateAsync(cancellationToken);
    }

    public async Task<SessionCommandResult> ExecuteAsync(SessionCommand request)
    {
        var turn = new Turn();
        SessionCommandResult? result;
        await _gate.WaitAsync();
        try { result = await ExecuteInGateAsync(request, turn); }
        finally { _gate.Release(); }
        if (result is not null) return result;

        // The answer is part of a batch that has not committed yet; acknowledge it only once it has.
        var waiter = turn.Waiter!;
        var outcome = await waiter.Completion.Task;
        if (outcome.Committed is null) return SessionCommandResult.Error(outcome.ErrorCode!, outcome.Retryable);
        try
        {
            var view = views.Render(outcome.Committed, turn.Format!, request.PlayerId, clock.Now);
            return new SessionCommandResult("ok", false, view.Version, view.Json);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Session command committed but rendering its view failed for {SessionId}", _sessionId);
            return SessionCommandResult.Error("view_unavailable");
        }
    }

    /// <summary>Returns null when the command's answer was deferred into a batch; <paramref name="turn"/> then holds its waiter.</summary>
    private async Task<SessionCommandResult?> ExecuteInGateAsync(SessionCommand request, Turn turn)
    {
        var started = clock.Now;
        SessionTurnKind? measuredKind = null;
        DateTimeOffset? turnDueAt = null;
        var activated = _state is null;
        try
        {
            _state ??= await snapshots.LoadAsync(_tenantId, _sessionId);
            if (_state is null)
            {
                Deactivate();
                return SessionCommandResult.Error("not_found");
            }
            ArmDeadlineTimer();
            if (_state.Session.Ended)
            {
                if (_state.Session.Status == "cancelled") return CancelledView("terminal", request.PlayerId);
                if (!formats.TryGet(_state.Session.Format ?? "", out var terminalFormat) || terminalFormat is null) return SessionCommandResult.Error("terminal");
                return View("terminal", terminalFormat, request.PlayerId);
            }
            await deadlines.ReconcileAsync(_state);
            if (await ReconcileAsync()) return SessionCommandResult.Error("terminal");
            if (_state.Session.RetryAt is { } retryAt && clock.Now < retryAt) return SessionCommandResult.Error("session_busy", retryable: true);
            if (!formats.TryGet(_state.Session.Format ?? "", out var format) || format is null)
                return SessionCommandResult.Error("format_unavailable");
            if (!_state.Seats.ContainsKey(request.PlayerId)) return SessionCommandResult.Error("absent_seat");

            if (!TryCommand(request, out var command)) return SessionCommandResult.Error("invalid_command");
            if (command.Revision < 0) return SessionCommandResult.Error("invalid_command");
            var position = format.Spec.Pacing == Pacing.Shared ? _state.Session.Position : _state.Seats[request.PlayerId].Position;
            if (position != command.At) return View("stale_position", format, request.PlayerId);
            await EnsureItemsAsync(command.At.Slot);
            if (!_state.Items.ContainsKey(command.At.Slot)) return SessionCommandResult.Error("not_found");
            if (command.At.Slot + 1 < _state.ItemCount) await EnsureItemsAsync(command.At.Slot + 1);
            if (command.At.Slot + 1 < _state.ItemCount && !_state.Items.ContainsKey(command.At.Slot + 1)) return SessionCommandResult.Error("not_found");
            // Finishing formats compute a durable result from every item, so load the
            // historical items only for their final transition.
            if (command.At.Slot == _state.ItemCount - 1) await EnsureItemsAsync(Enumerable.Range(0, _state.ItemCount).ToArray());
            if (command.Type == CommandType.Answer && _state.Session.DueAt is { } dueAt
                && (request.ReceivedAt > dueAt + format.Spec.Grace || clock.Now > dueAt + format.Spec.Grace))
                return View("late_answer", format, request.PlayerId);

            var previousPosition = _state.Session.Position;
            turnDueAt = _state.Session.DueAt;
            var generation = _generation;
            var transition = new SessionTransition(_state, kinds, clock, format.Spec.Grace, format.Spec.Scored);
            try { format.Apply(transition, command); }
            catch (SessionCommandException exception)
            {
                return AwaitOwnPendingAnswer(command, format, turn, activated, started) ? null : View(exception.Code, format, request.PlayerId);
            }
            catch (Exception exception) when (!IsInfrastructureFailure(exception))
            {
                await RecordFaultAsync(exception);
                return SessionCommandResult.Error("session_busy", retryable: true);
            }
            if (!transition.Changed)
            {
                // A repeat of an answer that is still waiting for its batch is not acknowledged before that batch commits.
                if (AwaitOwnPendingAnswer(command, format, turn, activated, started)) return null;
                // A cold command can load the whole session without changing the durable
                // state (the benchmark's initial countdown command does exactly that).
                // Preserve that activation cost as its own measurement rather than
                // dropping it with ordinary unchanged commands.
                measuredKind = SessionTurnClassifier.ForCommand(
                    activated,
                    changed: false,
                    command.Type == CommandType.Answer,
                    _state.Session.Position?.Phase,
                    _state.Session.Position?.Phase);
                return View("ok", format, request.PlayerId);
            }

            var current = transition.Current;
            if (IsOrdinaryAnswer(command, _state.Session, current.Session))
            {
                // Ordinary answers are graded and scored in memory now and written with their batch (7.4.1).
                _baseline ??= _state;
                _state = current;
                var waiter = new PendingAnswer(request.PlayerId, command.At.Slot, format, clock.Now - started, _state.Session.DueAt, measured: !activated, acceptedAt: clock.Now);
                _pending.Add(waiter);
                turn.Waiter = waiter;
                turn.Format = format;
                if (activated) _turnObserver.Record(new SessionTurnSample(SessionTurnKind.ColdActivation, clock.Now - started, _state.Session.DueAt, clock.Now));
                QueueFlush();
                return null;
            }
            measuredKind = SessionTurnClassifier.ForCommand(
                activated,
                changed: true,
                command.Type == CommandType.Answer,
                previousPosition?.Phase,
                current.Session.Position?.Phase);
            // This write must follow the batch that is being written and carries the answers still waiting.
            if (!await QuiesceFlushAsync(generation)) return SessionCommandResult.Error("write_failed", retryable: true);
            // The reentrant gate was yielded while the batch drained. A timeout or another command may have
            // committed (or extended the grace deadline) in that interval, so never persist a transition
            // calculated from the old working snapshot over that newer state.
            if (!ReferenceEquals(transition.Initial, _state))
            {
                // The retried command owns the observable turn measurement. Recording this discarded
                // transition as well would double-count a close or finish that was never persisted.
                measuredKind = null;
                return await ExecuteInGateAsync(request, turn);
            }
            var committedBase = _baseline ?? _state!; // the committed state, which a batch that just finished may have advanced
            long version;
            try
            {
                version = await store.SaveAsync(_tenantId, _sessionId, committedBase.Session.Version, committedBase, current);
            }
            catch (StaleActivationException) { throw; }
            catch (Exception exception) when (IsUnknownCommit(exception)) { throw; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Session command rolled back for {SessionId}", _sessionId);
                ResetToBaseline();
                FailPending("write_failed", retryable: true);
                return SessionCommandResult.Error("write_failed", retryable: true);
            }
            var committed = current with { Session = current.Session with { Version = version } };
            var waiting = TakePending();
            // Command responses use a new immutable durable snapshot.  This keeps the
            // command path on the same rendering contract as a future stream and prevents
            // a cached activation from combining the committed session row with old children.
            try
            {
                // The write has committed, so retain its candidate only while the durable
                // read is in flight. Any reload failure below immediately discards it.
                _state = committed;
                if (RequiresReload(format, transition.Initial.Session, current.Session))
                    _state = await snapshots.LoadAsync(_tenantId, _sessionId)
                        ?? throw new StaleActivationException();
                ArmDeadlineTimer();
                Complete(waiting, _state);
            }
            catch (StaleActivationException) { Complete(waiting, committed); throw; }
            catch (Exception exception) when (IsUnknownCommit(exception)) { Complete(waiting, committed); throw; }
            catch (Exception exception)
            {
                Complete(waiting, committed);
                logger.LogWarning(
                    exception,
                    "Session {SessionId} was committed at version {Version}, but its durable state could not be reloaded",
                    _sessionId,
                    version);
                DropState();
                return SessionCommandResult.Error("view_unavailable", retryable: true);
            }
            try { return View("ok", format, request.PlayerId); }
            catch (Exception exception)
            {
                logger.LogError(exception, "Session command committed but rendering its view failed for {SessionId}", _sessionId);
                return SessionCommandResult.Error("view_unavailable");
            }
        }
        catch (StaleActivationException)
        {
            DropState();
            return SessionCommandResult.Error("stale_activation", retryable: true);
        }
        catch (Exception exception) when (IsUnknownCommit(exception))
        {
            logger.LogWarning(exception, "Session command has an unknown commit outcome for {SessionId}", _sessionId);
            DropState();
            return SessionCommandResult.Error("retryable", retryable: true);
        }
        catch (Exception exception) when (IsInfrastructureFailure(exception))
        {
            logger.LogWarning(exception, "Session command infrastructure failed for {SessionId}", _sessionId);
            DropState();
            return SessionCommandResult.Error("retryable", retryable: true);
        }
        finally
        {
            if (measuredKind is { } kind)
            {
                _turnObserver.Record(new SessionTurnSample(kind, clock.Now - started, turnDueAt, clock.Now));
            }
        }
    }

    public async Task<LobbyCommandResult> ExecuteLobbyAsync(LobbyCommand request)
    {
        await _gate.WaitAsync();
        try { return await ExecuteLobbyInGateAsync(request); }
        finally { _gate.Release(); }
    }

    private async Task<LobbyCommandResult> ExecuteLobbyInGateAsync(LobbyCommand request)
    {
        if (lobby is null) return LobbyCommandResult.Error("lobby_unavailable");
        if (!Guid.TryParse(request.Player, out var playerId) || string.IsNullOrWhiteSpace(request.Type))
            return LobbyCommandResult.Error("invalid_request");
        _state ??= await snapshots.LoadAsync(_tenantId, _sessionId);
        if (_state is null) return LobbyCommandResult.Error("not_found");
        ArmDeadlineTimer();
        if (_state.Session.Ended) return LobbyCommandResult.Error(LobbyTerminalCode(_state));
        if (await ReconcileAsync()) return LobbyCommandResult.Error("expired");
        LobbyOutcome outcome;
        try
        {
            outcome = request.Type.ToLowerInvariant() switch
            {
                "join" => await lobby.JoinAsync(_tenantId, _sessionId, playerId),
                "leave" when string.Equals(_state.Session.Owner, request.Player, StringComparison.Ordinal) =>
                    await lobby.CancelAsync(_tenantId, _sessionId, playerId, store),
                "leave" => await lobby.LeaveAsync(_tenantId, _sessionId, playerId),
                "cancel" => await lobby.CancelAsync(_tenantId, _sessionId, playerId, store),
                "settings" => await lobby.ConfigureAsync(_tenantId, _sessionId, playerId, request.RulesJson, request.ContentJson, request.Capacity),
                "start" => await lobby.StartAsync(_tenantId, _sessionId, playerId),
                _ => LobbyOutcome.Error("invalid_request"),
            };
            if (outcome.Version is not null || outcome.Code == "ended")
            {
                _state = await snapshots.LoadAsync(_tenantId, _sessionId);
                ArmDeadlineTimer();
            }
        }
        catch (StaleActivationException)
        {
            DropState();
            return LobbyCommandResult.Error("stale_activation");
        }
        return new LobbyCommandResult(outcome.Code, outcome.Version);
    }

    public async Task TimeoutAsync(DateTimeOffset dueAt)
    {
        await _gate.WaitAsync();
        try { await TimeoutInGateAsync(dueAt); }
        finally { _gate.Release(); }
    }

    private async Task TimeoutInGateAsync(DateTimeOffset dueAt)
    {
        var started = clock.Now;
        SessionTurnKind? measuredKind = null;
        try
        {
            _state ??= await snapshots.LoadAsync(_tenantId, _sessionId);
            if (_state is null) { Deactivate(); return; }
            if (_state.Session.Ended || _state.Session.DueAt != dueAt || dueAt > clock.Now
                || _state.Session.RetryAt is { } retryAt && retryAt > clock.Now) { ArmDeadlineTimer(); return; }
            _turnObserver.RecordTimerLag(clock.Now - dueAt);
            if (ShouldExpire(_state))
            {
                await EndCoreAsync(CoreEndReason.Expired);
                return;
            }
            if (!formats.TryGet(_state.Session.Format ?? "", out var format) || format is null) return;
            if (format.MaximumDeadlineLateness is { } limit && clock.Now - dueAt > limit)
            {
                await EndCoreAsync(CoreEndReason.Outage);
                return;
            }
            var generation = _generation;
            var transition = new SessionTransition(_state, kinds, clock, format.Spec.Grace, format.Spec.Scored);
            try { format.Timeout(transition); }
            catch (Exception exception) when (!IsInfrastructureFailure(exception))
            {
                await RecordFaultAsync(exception);
                return;
            }
            // A timeout delivery is identified solely by its due_at value.  Consume that
            // identity even when the format leaves it unchanged so duplicate timer and
            // sweep deliveries cannot run the same timeout transition again.
            if (transition.Changed && transition.Current.Session.DueAt == dueAt) transition.ClearDeadline();
            if (!transition.Changed) { ArmDeadlineTimer(); return; }
            measuredKind = SessionTurnClassifier.ForTimeout(
                changed: true,
                _state.Session.Position?.Phase,
                transition.Current.Session.Position?.Phase,
                transition.Current.Session.Ended);
            if (!await QuiesceFlushAsync(generation)) return;
            // Quiescing yields the reentrant gate. Re-evaluate the delivery when the drained batch or a
            // concurrent command replaced the snapshot so this timeout cannot overwrite a newer transition.
            if (!ReferenceEquals(transition.Initial, _state))
            {
                // This delivery did not persist its transition. The recursive delivery below owns the
                // timer sample, so do not count this stale attempt as a second close or finish.
                measuredKind = null;
                await TimeoutInGateAsync(dueAt);
                return;
            }
            var committedBase = _baseline ?? _state!; // the committed state, which a batch that just finished may have advanced
            long version;
            try { version = await store.SaveAsync(_tenantId, _sessionId, committedBase.Session.Version, committedBase, transition.Current); }
            catch (Exception exception) when (!IsInfrastructureFailure(exception))
            {
                ResetToBaseline();
                FailPending("write_failed", retryable: true);
                throw;
            }
            var committed = transition.Current with { Session = transition.Current.Session with { Version = version } };
            var waiting = TakePending();
            _state = committed;
            try
            {
                if (RequiresReload(format, transition.Initial.Session, transition.Current.Session))
                {
                    _state = await snapshots.LoadAsync(_tenantId, _sessionId) ?? throw new StaleActivationException();
                    _state = _state with { Session = _state.Session with { Version = version } };
                }
                ArmDeadlineTimer();
            }
            finally { Complete(waiting, _state ?? committed); }
        }
        catch (Exception exception) when (IsInfrastructureFailure(exception))
        {
            logger.LogWarning(exception, "Session timeout has an uncertain persistence outcome for {SessionId}", _sessionId);
            DropState();
            throw;
        }
        finally
        {
            if (measuredKind is { } kind)
            {
                _turnObserver.Record(new SessionTurnSample(kind, clock.Now - started, dueAt, clock.Now, SessionTurnSource.Timer));
            }
        }
    }

    private async Task<bool> ReconcileAsync()
    {
        if (ShouldExpire(_state!))
        {
            await EndCoreAsync(CoreEndReason.Expired);
            return true;
        }
        if (_state!.Session.DueAt is { } dueAt && dueAt <= clock.Now && !(_state.Session.RetryAt is { } retryAt && retryAt > clock.Now))
        {
            await TimeoutInGateAsync(dueAt);
            return _state is null || _state.Session.Ended;
        }
        return false;
    }

    private static string LobbyTerminalCode(SessionState state) =>
        state.Session.EndReason == EndReason.Expired ? "expired" : state.Session.Status == "cancelled" ? "cancelled" : "terminal";

    private bool ShouldExpire(SessionState state) => state.Session.StartedAt is { } started && clock.Now >= started.AddDays(90)
        || state.Session.Status == "lobby" && state.Session.DueAt is { } lobbyDue && lobbyDue <= clock.Now && !HasFormatLobbyDeadline(state.Session);

    // A format that declares its own lobby deadline decides how that deadline ends the lobby; every other lobby expires through the core ending.
    private bool HasFormatLobbyDeadline(SessionRow session) =>
        formats.TryGet(session.Format ?? "", out var format) && format is not null && format.LobbyDeadline(session) is not null;

    private async Task RecordFaultAsync(Exception exception)
    {
        var count = _state!.Session.FaultCount + 1;
        var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Max(0, count - 1))));
        logger.LogWarning(exception, "Format or kind failed for {SessionId}; retrying after {Delay}", _sessionId, delay);
        await store.RecordFaultAsync(_tenantId, _sessionId, _state.Session.Version, clock.Now.Add(delay));
        // Reloading discards the deferred answers, which were never written.
        _baseline = null;
        FailPending("session_busy", retryable: true);
        _state = await snapshots.LoadAsync(_tenantId, _sessionId);
        ArmDeadlineTimer();
    }

    private void ArmDeadlineTimer()
    {
        var session = _state?.Session;
        if (_tenantId == Guid.Empty || session is null || session.Ended || session.DueAt is null)
        {
            DisposeDeadlineTimer();
            return;
        }
        var dueAt = session.DueAt.Value;
        var wakeAt = session.RetryAt is { } retryAt && retryAt > dueAt ? retryAt : dueAt;
        if (_timerWakeAt == wakeAt && _timerDueAt == dueAt) return;
        DisposeDeadlineTimer();
        if (wakeAt - clock.Now > TimeSpan.FromMinutes(2)) return;
        _timerWakeAt = wakeAt;
        _timerDueAt = dueAt;
        _deadlineTimer = this.RegisterGrainTimer(
            cancellationToken => TimeoutAsync(dueAt),
            new GrainTimerCreationOptions(wakeAt > clock.Now ? wakeAt - clock.Now : TimeSpan.Zero, Timeout.InfiniteTimeSpan)
            { Interleave = false, KeepAlive = false });
    }

    private void DisposeDeadlineTimer()
    {
        _deadlineTimer?.Dispose();
        _deadlineTimer = null;
        _timerWakeAt = null;
        _timerDueAt = null;
    }

    private SessionCommandResult View(string code, IFormat format, string viewer)
    {
        // Answers still waiting for their batch are not committed, so a view never shows them.
        var view = views.Render(_baseline ?? _state!, format, viewer, clock.Now);
        return new SessionCommandResult(code, false, view.Version, view.Json);
    }

    private SessionCommandResult CancelledView(string code, string viewer)
    {
        var view = views.RenderCancelled(_state!, viewer, clock.Now);
        return new SessionCommandResult(code, false, view.Version, view.Json);
    }

    private static bool TryCommand(SessionCommand source, out Command command)
    {
        command = default!;
        if (source.Slot < 0 || source.Step < 0 || source.Revision is < 0
            || !Enum.TryParse<CommandType>(source.Type, true, out var type)
            || !TryPhase(source.Phase, out var phase)) return false;
        JsonElement? payload = null;
        if (source.PayloadJson is { } raw)
        {
            try { payload = JsonDocument.Parse(raw).RootElement.Clone(); }
            catch (JsonException) { return false; }
        }
        command = new(type, source.PlayerId, new Position(phase, source.Slot, source.Step), source.Revision ?? 0, source.Name, payload, source.ReceivedAt);
        return true;
    }
    private static bool TryPhase(string value, out int phase) => int.TryParse(value, out phase) || (phase = value.ToLowerInvariant() switch { "lobby" or "idle" => -1, _ => int.MinValue }) != int.MinValue;
    private static bool IsUnknownCommit(Exception exception) => exception is TenantCommitOutcomeUnknownException;
    private static bool IsInfrastructureFailure(Exception exception) => exception is TenantCommitOutcomeUnknownException
        or StaleActivationException or TimeoutException or OperationCanceledException or System.Data.Common.DbException;
    private async Task EnsureItemsAsync(params int[] slots)
    {
        var missing = slots.Where(slot => slot >= 0 && !_state!.Items.ContainsKey(slot)).Distinct().ToArray();
        if (missing.Length == 0) return;
        var loaded = await snapshots.LoadItemsAsync(_tenantId, _sessionId, missing);
        // A failed batch discards the state while the items load.
        _state = (_state ?? throw new StaleActivationException()).WithItems(loaded);
    }
    private void DropState()
    {
        DisposeDeadlineTimer();
        _state = null;
        _baseline = null;
        _generation++;
        Fail(_inflight, "retryable", retryable: true);
        _inflight = [];
        FailPending("retryable", retryable: true);
        Deactivate();
    }

    private async Task EndCoreAsync(CoreEndReason reason)
    {
        await QuiesceFlushAsync(_generation);
        await store.EndCoreAsync(_tenantId, _sessionId, reason);
        // The deferred answers were never written and the session is over.
        _baseline = null;
        FailPending("terminal", retryable: false);
        _state = await snapshots.LoadAsync(_tenantId, _sessionId);
        ArmDeadlineTimer();
    }

    /// <summary>
    /// The state just written is what the database holds, so it is kept as it is unless the transition moved to another
    /// slot (or the format paces seats individually): the loader holds only the answers of the slots that are open, and
    /// reloading drops the rest.
    /// </summary>
    private static bool RequiresReload(IFormat format, SessionRow before, SessionRow after) =>
        !after.Ended && (format.Spec.Pacing != Pacing.Shared || after.Position?.Slot != before.Position?.Slot);

    /// <summary>An ordinary answer changes only answers, scores and format state; anything that moves the session is written at once.</summary>
    private static bool IsOrdinaryAnswer(Command command, SessionRow before, SessionRow after) =>
        command.Type == CommandType.Answer
        && !after.Ended
        && after.Position == before.Position
        && after.DueAt == before.DueAt
        && after.PositionStartedAt == before.PositionStartedAt
        && after.Status == before.Status;

    private bool AwaitOwnPendingAnswer(Command command, IFormat format, Turn turn, bool activated, DateTimeOffset started)
    {
        if (command.Type != CommandType.Answer) return false;
        bool Own(PendingAnswer waiting) => waiting.Player == command.Player && waiting.Slot == command.At.Slot;
        // A repeat of an answer whose batch is being written waits for that batch's outcome, never for a later one.
        if (_inflight.FirstOrDefault(Own) is { } writing)
        {
            turn.Waiter = writing;
            turn.Format = format;
            return true;
        }
        if (!_pending.Any(Own)) return false;
        var follower = new PendingAnswer(command.Player, command.At.Slot, format, clock.Now - started, _state!.Session.DueAt, measured: false, acceptedAt: clock.Now);
        _pending.Add(follower);
        turn.Waiter = follower;
        turn.Format = format;
        return true;
    }

    private void QueueFlush()
    {
        if (_flushIdle is not null) return;
        _flushIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = FlushLoopAsync(_flushIdle);
    }

    /// <summary>
    /// Writes accepted answers one batch at a time. The database await is outside the gate, but both sides of it
    /// acquire the gate before observing or changing session state, so a reentrant continuation cannot overtake a
    /// command or timeout that already owns the gate.
    /// </summary>
    private async Task FlushLoopAsync(TaskCompletionSource idle)
    {
        try
        {
            while (true)
            {
                int generation;
                SessionState baseline;
                SessionState snapshot;
                List<PendingAnswer> batch;

                await _gate.WaitAsync();
                try
                {
                    if (_exclusive || _pending.Count == 0 || _state is null || _baseline is null) return;
                    generation = _generation;
                    baseline = _baseline;
                    snapshot = _state;
                    batch = _pending;
                    _pending = [];
                    _inflight = batch;
                }
                finally
                {
                    _gate.Release();
                }

                long version = 0;
                Exception? writeFailure = null;
                try
                {
                    version = await store.SaveAsync(_tenantId, _sessionId, baseline.Session.Version, baseline, snapshot);
                }
                catch (Exception exception)
                {
                    writeFailure = exception;
                }

                await _gate.WaitAsync();
                try
                {
                    if (writeFailure is not null)
                    {
                        if (generation != _generation)
                        {
                            logger.LogWarning(writeFailure, "Answer batch failed for {SessionId} after its state was discarded", _sessionId);
                            return;
                        }
                        if (writeFailure is StaleActivationException)
                        {
                            Fail(batch, "stale_activation", retryable: true);
                            DropState();
                            return;
                        }
                        if (IsUnknownCommit(writeFailure))
                        {
                            logger.LogWarning(writeFailure, "Answer batch has an unknown commit outcome for {SessionId}", _sessionId);
                            Fail(batch, "retryable", retryable: true);
                            DropState();
                            return;
                        }

                        // Rolled back for certain. Answers accepted meanwhile were graded on top of the lost ones.
                        logger.LogWarning(writeFailure, "Answer batch rolled back for {SessionId}", _sessionId);
                        _inflight = [];
                        _state = baseline;
                        _baseline = null;
                        _generation++;
                        Fail(batch, "write_failed", retryable: true);
                        FailPending("write_failed", retryable: true);
                        return;
                    }

                    if (generation != _generation) return;
                    var committed = snapshot with { Session = snapshot.Session with { Version = version } };
                    _inflight = [];
                    if (_pending.Count == 0)
                    {
                        _state = committed;
                        _baseline = null;
                    }
                    else
                    {
                        _baseline = committed;
                        _state = _state with { Session = _state.Session with { Version = version } };
                    }
                    Complete(batch, committed);
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (Exception exception)
        {
            await _gate.WaitAsync();
            try
            {
                logger.LogError(exception, "Flushing answers failed for {SessionId}", _sessionId);
                DropState();
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            await _gate.WaitAsync();
            try
            {
                _exclusive = false;
                _flushIdle = null;
                idle.TrySetResult();
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <summary>
    /// Lets the batch being written finish and keeps further batches from starting. The caller owns the gate, so it
    /// yields it while waiting for the flush continuation to finish under that same gate. False when the batch
    /// failed, so the state the caller computed from is gone.
    /// </summary>
    private async Task<bool> QuiesceFlushAsync(int generation)
    {
        if (_flushIdle is { } idle)
        {
            _exclusive = true;
            _gate.Release();
            try
            {
                await idle.Task;
            }
            finally
            {
                await _gate.WaitAsync();
            }
        }
        return generation == _generation;
    }

    private List<PendingAnswer> TakePending()
    {
        var batch = _pending;
        _pending = [];
        _baseline = null;
        return batch;
    }

    private void ResetToBaseline()
    {
        if (_baseline is not null) _state = _baseline;
        _baseline = null;
        _generation++;
    }

    private void FailPending(string code, bool retryable) => Fail(TakePending(), code, retryable);

    private static void Fail(List<PendingAnswer> batch, string code, bool retryable)
    {
        foreach (var answer in batch) answer.Completion.TrySetResult(new BatchOutcome(null, code, retryable));
    }

    /// <summary>
    /// Acknowledges a committed batch. An ordinary answer's measured turn is the time it held the gate; the wait for
    /// its commit is recorded separately, because the grain is free to take other commands while a batch commits (§7.4.1).
    /// </summary>
    private void Complete(List<PendingAnswer> batch, SessionState committed)
    {
        foreach (var answer in batch)
        {
            if (answer.Measured)
            {
                _turnObserver.Record(new SessionTurnSample(SessionTurnKind.OrdinaryAnswer, answer.OwnTurn, answer.DueAt, clock.Now));
                _turnObserver.RecordAnswerCommit(clock.Now - answer.AcceptedAt);
            }
            answer.Completion.TrySetResult(new BatchOutcome(committed, null, false));
        }
    }

    private sealed class Turn
    {
        public PendingAnswer? Waiter { get; set; }
        public IFormat? Format { get; set; }
    }

    private sealed class PendingAnswer(string player, int slot, IFormat format, TimeSpan ownTurn, DateTimeOffset? dueAt, bool measured, DateTimeOffset acceptedAt)
    {
        public DateTimeOffset AcceptedAt { get; } = acceptedAt;
        public string Player { get; } = player;
        public int Slot { get; } = slot;
        public IFormat Format { get; } = format;
        public TimeSpan OwnTurn { get; } = ownTurn;
        public DateTimeOffset? DueAt { get; } = dueAt;
        public bool Measured { get; } = measured;
        public TaskCompletionSource<BatchOutcome> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record BatchOutcome(SessionState? Committed, string? ErrorCode, bool Retryable);

    /// <summary>Requests that the activation be discarded after its cached state becomes invalid.</summary>
    protected virtual void Deactivate() => DeactivateOnIdle();
}
