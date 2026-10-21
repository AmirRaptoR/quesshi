using System.Text.Json;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Quesshi.Application.Sessions;
using Quesshi.Engine.Contracts;
using Quesshi.Grains.Abstractions;
using Quesshi.Plugin.Api;
using Quesshi.Shared;

namespace Quesshi.Grains;

/// <summary>The sole in-memory writer for one tenant session. Orleans grains are non-reentrant by default.</summary>
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

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var parts = this.GetPrimaryKeyString().Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out _tenantId) || !Guid.TryParse(parts[1], out _sessionId))
            throw new ArgumentException("Session grain keys must be '{tenantId}/{sessionId}'.");
        return base.OnActivateAsync(cancellationToken);
    }

    public async Task<SessionCommandResult> ExecuteAsync(SessionCommand request)
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
            var transition = new SessionTransition(_state, kinds, clock, format.Spec.Grace, format.Spec.Scored);
            try { format.Apply(transition, command); }
            catch (SessionCommandException exception) { return View(exception.Code, format, request.PlayerId); }
            catch (Exception exception) when (!IsInfrastructureFailure(exception))
            {
                await RecordFaultAsync(exception);
                return SessionCommandResult.Error("session_busy", retryable: true);
            }
            if (!transition.Changed)
            {
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
            measuredKind = SessionTurnClassifier.ForCommand(
                activated,
                changed: true,
                command.Type == CommandType.Answer,
                previousPosition?.Phase,
                current.Session.Position?.Phase);
            long version;
            try
            {
                version = await store.SaveAsync(_tenantId, _sessionId, _state.Session.Version, transition.Initial, current);
            }
            catch (StaleActivationException) { throw; }
            catch (Exception exception) when (IsUnknownCommit(exception)) { throw; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Session command rolled back for {SessionId}", _sessionId);
                return SessionCommandResult.Error("write_failed", retryable: true);
            }
            // Command responses use a new immutable durable snapshot.  This keeps the
            // command path on the same rendering contract as a future stream and prevents
            // a cached activation from combining the committed session row with old children.
            try
            {
                // The write has committed, so retain its candidate only while the durable
                // read is in flight. Any reload failure below immediately discards it.
                _state = current with { Session = current.Session with { Version = version } };
                _state = await snapshots.LoadAsync(_tenantId, _sessionId)
                    ?? throw new StaleActivationException();
                ArmDeadlineTimer();
            }
            catch (StaleActivationException) { throw; }
            catch (Exception exception) when (IsUnknownCommit(exception)) { throw; }
            catch (Exception exception)
            {
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
        if (lobby is null) return LobbyCommandResult.Error("lobby_unavailable");
        if (!Guid.TryParse(request.Player, out var playerId) || string.IsNullOrWhiteSpace(request.Type))
            return LobbyCommandResult.Error("invalid_request");
        _state ??= await snapshots.LoadAsync(_tenantId, _sessionId);
        if (_state is null) return LobbyCommandResult.Error("not_found");
        ArmDeadlineTimer();
        if (_state.Session.Ended) return LobbyCommandResult.Error(_state.Session.Status == "cancelled" ? "cancelled" : "terminal");
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
                await store.EndCoreAsync(_tenantId, _sessionId, CoreEndReason.Expired);
                _state = await snapshots.LoadAsync(_tenantId, _sessionId);
                ArmDeadlineTimer();
                return;
            }
            if (!formats.TryGet(_state.Session.Format ?? "", out var format) || format is null) return;
            if (format.MaximumDeadlineLateness is { } limit && clock.Now - dueAt > limit)
            {
                await store.EndCoreAsync(_tenantId, _sessionId, CoreEndReason.Outage);
                _state = await snapshots.LoadAsync(_tenantId, _sessionId);
                ArmDeadlineTimer();
                return;
            }
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
            var version = await store.SaveAsync(_tenantId, _sessionId, _state.Session.Version, transition.Initial, transition.Current);
            _state = await snapshots.LoadAsync(_tenantId, _sessionId) ?? throw new StaleActivationException();
            _state = _state with { Session = _state.Session with { Version = version } };
            ArmDeadlineTimer();
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
            await store.EndCoreAsync(_tenantId, _sessionId, CoreEndReason.Expired);
            _state = await snapshots.LoadAsync(_tenantId, _sessionId);
            ArmDeadlineTimer();
            return true;
        }
        if (_state!.Session.DueAt is { } dueAt && dueAt <= clock.Now && !(_state.Session.RetryAt is { } retryAt && retryAt > clock.Now))
        {
            await TimeoutAsync(dueAt);
            return _state is null || _state.Session.Ended;
        }
        return false;
    }

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
        var view = views.Render(_state!, format, viewer, clock.Now);
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
        _state = _state!.WithItems(await snapshots.LoadItemsAsync(_tenantId, _sessionId, missing));
    }
    private void DropState() { DisposeDeadlineTimer(); _state = null; Deactivate(); }

    /// <summary>Requests that the activation be discarded after its cached state becomes invalid.</summary>
    protected virtual void Deactivate() => DeactivateOnIdle();
}
