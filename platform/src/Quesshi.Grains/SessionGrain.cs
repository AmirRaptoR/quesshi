using System.Text.Json;
using Microsoft.Extensions.Logging;
using Orleans;
using Quesshi.Application.Sessions;
using Quesshi.Engine.Contracts;
using Quesshi.Grains.Abstractions;
using Quesshi.Shared;

namespace Quesshi.Grains;

/// <summary>The sole in-memory writer for one tenant session. Orleans grains are non-reentrant by default.</summary>
public sealed class SessionGrain(
    ISessionSnapshotLoader snapshots,
    SessionStateStore store,
    IFormatRegistry formats,
    IKindRegistry kinds,
    IClock clock,
    IViewRenderer views,
    ISessionDeadlineReconciler deadlines,
    ILogger<SessionGrain> logger) : Grain, ISessionGrain
{
    private SessionState? _state;
    private Guid _tenantId;
    private Guid _sessionId;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var parts = this.GetPrimaryKeyString().Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out _tenantId) || !Guid.TryParse(parts[1], out _sessionId))
            throw new ArgumentException("Session grain keys must be '{tenantId}/{sessionId}'.");
        return base.OnActivateAsync(cancellationToken);
    }

    public async Task<SessionCommandResult> ExecuteAsync(SessionCommand request)
    {
        try
        {
            _state ??= await snapshots.LoadAsync(_tenantId, _sessionId);
            if (_state is null)
            {
                DeactivateOnIdle();
                return SessionCommandResult.Error("not_found");
            }
            if (!formats.TryGet(_state.Session.Format ?? "", out var format) || format is null)
                return SessionCommandResult.Error("format_unavailable");
            if (!_state.Seats.ContainsKey(request.PlayerId)) return SessionCommandResult.Error("absent_seat");

            if (_state.Session.Ended) return View("terminal", format, request.PlayerId);
            await deadlines.ReconcileAsync(_state);
            if (_state.Session.RetryAt is { } retryAt && clock.Now < retryAt) return SessionCommandResult.Error("session_busy", retryable: true);

            if (!TryCommand(request, out var command)) return SessionCommandResult.Error("invalid_command");
            var position = format.Spec.Pacing == Pacing.Shared ? _state.Session.Position : _state.Seats[request.PlayerId].Position;
            if (position != command.At) return View("stale_position", format, request.PlayerId);
            if (!_state.Items.ContainsKey(command.At.Slot)) return SessionCommandResult.Error("not_found");
            if (_state.Items.Keys.Any(slot => slot > command.At.Slot) && !_state.Items.ContainsKey(command.At.Slot + 1)) return SessionCommandResult.Error("not_found");
            if (command.Type == CommandType.Answer && _state.Session.DueAt is { } dueAt && request.ReceivedAt > dueAt + format.Spec.Grace)
                return View("late_answer", format, request.PlayerId);

            var transition = new SessionTransition(_state, kinds, clock, format.Spec.Grace);
            try { format.Apply(transition, command); }
            catch (Exception exception) when (exception.Message == "revision_conflict") { return View("revision_conflict", format, request.PlayerId); }
            if (!transition.Changed) return View("ok", format, request.PlayerId);

            var current = transition.Current;
            long version;
            try
            {
                version = await store.SaveAsync(_tenantId, _sessionId, _state.Session.Version, current);
            }
            catch (StaleActivationException) { throw; }
            catch (Exception exception) when (IsUnknownCommit(exception)) { throw; }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Session command rolled back for {SessionId}", _sessionId);
                return SessionCommandResult.Error("write_failed", retryable: true);
            }
            _state = current with { Session = current.Session with { Version = version } };
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
    }

    private SessionCommandResult View(string code, IFormat format, string viewer)
    {
        var view = views.Render(_state!, format, viewer);
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
    private static bool IsUnknownCommit(Exception exception) => exception is System.IO.IOException or TimeoutException or System.Data.Common.DbException;
    private void DropState() { _state = null; DeactivateOnIdle(); }
}
