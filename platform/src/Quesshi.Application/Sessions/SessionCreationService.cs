using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Quesshi.Application.Content;
using Quesshi.Engine.Contracts;
using Quesshi.Plugin.Api;
using Quesshi.Shared;

namespace Quesshi.Application.Sessions;

/// <summary>Creates an idempotent session and its initial durable snapshot in one tenant transaction.</summary>
internal sealed class SessionCreationService(
    IFormatRegistry formats,
    ISourceRegistry sources,
    SessionSnapshotFactory snapshots,
    IClock clock,
    ISessionCodeGenerator codes) : ISessions
{
    public async Task<SessionCreateResult> CreateAsync(ITenantTx transaction, Guid creatorId, SessionCreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Id == Guid.Empty || creatorId == Guid.Empty || string.IsNullOrWhiteSpace(request.Format)
            || string.IsNullOrWhiteSpace(request.Content.Source) || request.Content.Count < 1
            || request.Rules.ValueKind != JsonValueKind.Object || request.Content.Parameters.ValueKind != JsonValueKind.Object)
            return Invalid("invalid_request");

        var replay = await ReplayAsync(transaction, creatorId, request, cancellationToken);
        if (replay is not null) return replay;
        if (!formats.TryGet(request.Format, out var format) || format is null) return new(SessionCreateStatus.FormatUnavailable, Error: "format_unavailable");
        if (!await EnabledAsync(transaction, "format", request.Format, cancellationToken)) return new(SessionCreateStatus.FormatUnavailable, Error: "format_unavailable");

        JsonElement rules;
        DrawPlan plan;
        try
        {
            rules = format.ReadRules(request.Rules);
            plan = format.Plan(new ContentSpec(request.Content.Source, request.Content.Parameters, request.Content.Count), rules);
        }
        catch (ArgumentException) { return Invalid("invalid_rules"); }
        catch (JsonException) { return Invalid("invalid_rules"); }

        if (plan.Count != request.Content.Count || plan.Count < 1 || request.Capacity < format.Spec.MinPlayers
            || request.Capacity > format.Spec.MaxPlayers || request.Capacity > await TenantCapacityAsync(transaction, cancellationToken))
            return Invalid("invalid_capacity");
        if (request.Shape == SessionShape.Lobby && !format.Spec.Lobby) return Invalid("lobby_not_supported");

        var roster = Roster(creatorId, request);
        if (roster is null || roster.Count > request.Capacity)
            return Invalid("invalid_roster");
        if (roster.Select(x => x.PlayerId).Distinct().Count() != roster.Count
            || roster.Any(x => !format.Spec.Roles.Any(role => string.Equals(role.Id, x.Role, StringComparison.Ordinal))))
            return Invalid("invalid_roster");
        var playing = roster.Count(participant => format.Spec.Roles.Single(role => role.Id == participant.Role).Plays);
        if (request.Shape == SessionShape.Started && (playing < format.Spec.MinPlayers || playing > format.Spec.MaxPlayers))
            return Invalid("invalid_roster");

        await snapshots.LockPlayersAsync(transaction, roster.Select(x => x.PlayerId), cancellationToken);
        var players = (await transaction.QueryAsync<PlayerData>("""
            select id as Id, lang as Language, account_id is null as IsGuest
            from players where id = any(@ids) and banned_at is null and deleted_at is null
            """, new { ids = roster.Select(x => x.PlayerId).ToArray() }, cancellationToken)).ToDictionary(x => x.Id);
        if (players.Count != roster.Count || !players.TryGetValue(creatorId, out var creator)) return Invalid("invalid_player");
        if (!format.Spec.Guests && players.Values.Any(player => player.IsGuest)) return Invalid("guest_not_allowed");
        var now = clock.Now;
        var seatValues = roster.Select((participant, number) => new Seat(
            participant.PlayerId.ToString(), participant.Role, Number: number, IsGuest: players[participant.PlayerId].IsGuest)).ToArray();
        var items = Array.Empty<PersistedSessionItem>();

        try
        {
            if (request.Shape != SessionShape.Lobby)
            {
                if (!sources.TryGet(request.Content.Source, out var source)) return new(SessionCreateStatus.SourceUnavailable, Error: "source_unavailable");
                if (!await EnabledAsync(transaction, "source", request.Content.Source, cancellationToken))
                    return new(SessionCreateStatus.SourceUnavailable, Error: "source_unavailable");
                if (!await source!.AuthorizeAsync(transaction, request.Content.Parameters, creatorId, cancellationToken))
                    return new(SessionCreateStatus.SourceUnavailable, Error: "source_unavailable");
                var sourcePlan = snapshots.Plan(plan, creator.Language, request.Id);
                items = await snapshots.DrawAsync(transaction, request.Id, source, request.Content.Parameters, sourcePlan, creatorId, seatValues, cancellationToken);
            }

            var session = new SessionRow(request.Id.ToString(), request.Shape == SessionShape.Lobby ? null : now, null, null,
                CreatedAt: now, Format: format.Id, Status: request.Shape == SessionShape.Lobby ? "lobby" : "running", Content: ContentElement(request.Content), Rules: rules);
            var state = SessionState.Create(session, Empty, seatValues, items.Select(item => item.Value), plan.Count);
            if (request.Shape == SessionShape.Lobby)
            {
                var due = format.LobbyDeadline(state.Session);
                state = state with { Session = state.Session with { DueAt = due } };
            }
            else
            {
                var transition = new SessionTransition(state, kinds, clock, format.Spec.Grace, format.Spec.Scored);
                format.Start(transition);
                state = transition.Current;
            }

            var created = await InsertWithCodeAsync(transaction, creatorId, request, roster, creator.Language, now, cancellationToken);
            if (created is null)
            {
                var afterCollision = await ReplayAsync(transaction, creatorId, request, cancellationToken);
                return afterCollision ?? new(SessionCreateStatus.IdConflict, Error: "id_conflict");
            }
            state = state with { Session = state.Session with { Code = created } };
            await PersistSeatsAsync(transaction, request.Id, state.Seats.Values, cancellationToken);
            await SessionSnapshotFactory.PersistAsync(transaction, request.Id, items, cancellationToken);
            await PersistSessionAsync(transaction, request.Id, state, format.Versions, cancellationToken);
            return new(SessionCreateStatus.Created, request.Id, created);
        }
        catch (NotEnoughQuestionsException) { return new(SessionCreateStatus.NotEnoughQuestions, Error: "not_enough_questions"); }
        catch (SourceUnavailableException) { return new(SessionCreateStatus.SourceUnavailable, Error: "source_unavailable"); }
        catch (InvalidSourceResultException) { return Invalid("invalid_source_result"); }
        catch (ArgumentException) { return Invalid("invalid_start"); }
        catch (InvalidOperationException) { return Invalid("invalid_start"); }
    }

    private async Task<SessionCreateResult?> ReplayAsync(ITenantTx transaction, Guid creatorId, SessionCreateRequest request, CancellationToken cancellationToken)
    {
        var expected = Roster(creatorId, request);
        if (expected is null) return await IdConflictIfExistsAsync(transaction, request.Id, cancellationToken);

        var equal = await transaction.QuerySingleOrDefaultAsync<ReplayRow>("""
            select code as Code
            from sessions
            where id = @id and owner_id = @creatorId and format = @format and capacity = @capacity
              and content = @content and rules = @rules
              and creation_shape = @shape and creation_roster = @roster
            """, new { id = request.Id, creatorId, format = request.Format, capacity = request.Capacity,
                content = Json(request.Content), rules = new Jsonb<JsonElement>(request.Rules, SessionJsonContext.Default.JsonElement),
                shape = Shape(request.Shape), roster = Json(expected) }, cancellationToken);
        if (equal is not null) return new(SessionCreateStatus.Replayed, request.Id, equal.Code);
        return await IdConflictIfExistsAsync(transaction, request.Id, cancellationToken);
    }

    private static IReadOnlyList<SessionParticipant>? Roster(Guid creator, SessionCreateRequest request) => request.Shape switch
    {
        SessionShape.Lobby or SessionShape.Open => [new SessionParticipant(creator)],
        SessionShape.Started when request.Participants is { Count: > 0 } && request.Participants.Any(x => x.PlayerId == creator) => request.Participants,
        _ => null,
    };

    private async Task<string?> InsertWithCodeAsync(ITenantTx transaction, Guid creatorId, SessionCreateRequest request, IReadOnlyList<SessionParticipant> roster, string language, DateTimeOffset now, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt != 32; attempt++)
        {
            var code = codes.Next();
            var inserted = await transaction.QuerySingleOrDefaultAsync<Guid?>("""
                insert into sessions (tenant_id, id, code, format, owner_id, lang, status, capacity, content, rules, creation_shape, creation_roster, created_at, started_at)
                values (app_tenant(), @id, @code, @format, @creatorId, @language, @status, @capacity, @content, @rules, @shape, @roster, @now, @startedAt)
                on conflict do nothing returning id
                """, new { id = request.Id, code, format = request.Format, creatorId, language,
                    status = request.Shape == SessionShape.Lobby ? "lobby" : "running", capacity = request.Capacity,
                    content = Json(request.Content), rules = new Jsonb<JsonElement>(request.Rules, SessionJsonContext.Default.JsonElement),
                    shape = Shape(request.Shape), roster = Json(roster), now,
                    startedAt = request.Shape == SessionShape.Lobby ? (DateTimeOffset?)null : now }, cancellationToken);
            if (inserted is not null) return code;
            var exists = await transaction.QuerySingleOrDefaultAsync<Guid?>("select id from sessions where id = @id", new { id = request.Id }, cancellationToken);
            if (exists is not null) return null;
        }
        throw new InvalidOperationException("Could not allocate a session code.");
    }

    private static async Task PersistSeatsAsync(ITenantTx transaction, Guid sessionId, IEnumerable<Seat> seats, CancellationToken cancellationToken)
    {
        foreach (var seat in seats)
            await transaction.ExecuteAsync("""
                insert into session_participants (tenant_id, session_id, player_id, seat, role, eligible, phase, slot, step)
                values (app_tenant(), @sessionId, @playerId, @number, @role, @eligible, @phase, @slot, @step)
                """, new { sessionId, playerId = Guid.Parse(seat.Player), number = seat.Number, role = seat.Role, eligible = seat.Eligible,
                    phase = seat.Position?.Phase.ToString() ?? "idle", slot = seat.Position?.Slot ?? -1, step = seat.Position?.Step ?? 0 }, cancellationToken);
    }

    private static Task PersistSessionAsync(ITenantTx transaction, Guid sessionId, SessionState state, FormatVersions versions, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync("""
            update sessions set status = @status, due_at = @dueAt, phase = @phase, slot = @slot, step = @step,
                phase_started_at = @phaseStartedAt, state = @state, state_version = @stateVersion
            where id = @sessionId
            """, new { sessionId, status = state.Session.Status, dueAt = state.Session.DueAt, phase = state.Session.Position?.Phase.ToString() ?? "lobby",
                slot = state.Session.Position?.Slot ?? -1, step = state.Session.Position?.Step ?? 0, phaseStartedAt = state.Session.PositionStartedAt,
                state = new Jsonb<JsonElement>(state.FormatState, SessionJsonContext.Default.JsonElement), stateVersion = versions.State }, cancellationToken);

    private async Task<int> TenantCapacityAsync(ITenantTx transaction, CancellationToken cancellationToken) => Math.Clamp(
        await transaction.QuerySingleOrDefaultAsync<int?>("""
            select coalesce((select (settings ->> 'capacity')::int from tenant_capabilities
                             where type = 'switch' and key = 'sessions'), 20)
            """, cancellationToken: cancellationToken) ?? 20, 1, 500);
    private static async Task<bool> EnabledAsync(ITenantTx transaction, string type, string key, CancellationToken cancellationToken) =>
        await transaction.QuerySingleOrDefaultAsync<bool?>("select entitled and enabled from tenant_capabilities where type = @type and key = @key", new { type, key }, cancellationToken) == true;
    private static Jsonb<SessionContent> Json(SessionContent content) => new(content, SessionJsonContext.Default.SessionContent);
    private static Jsonb<SessionParticipant[]> Json(IReadOnlyList<SessionParticipant> roster) => new(roster.ToArray(), SessionJsonContext.Default.SessionParticipantArray);
    private static string Shape(SessionShape shape) => shape.ToString().ToLowerInvariant();
    private static async Task<SessionCreateResult?> IdConflictIfExistsAsync(ITenantTx transaction, Guid id, CancellationToken cancellationToken)
    {
        var exists = await transaction.QuerySingleOrDefaultAsync<Guid?>("select id from sessions where id = @id", new { id }, cancellationToken);
        return exists is null ? null : new(SessionCreateStatus.IdConflict, Error: "id_conflict");
    }
    private static JsonElement ContentElement(SessionContent content) => JsonSerializer.SerializeToElement(content, SessionJsonContext.Default.SessionContent);
    private static SessionCreateResult Invalid(string error) => new(SessionCreateStatus.Invalid, Error: error);
    private static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement.Clone();
    private sealed record PlayerData(Guid Id, string Language, bool IsGuest);
    private sealed record ReplayRow(string Code);
}

[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(SessionContent))]
[JsonSerializable(typeof(SessionParticipant[]))]
internal sealed partial class SessionJsonContext : JsonSerializerContext;
