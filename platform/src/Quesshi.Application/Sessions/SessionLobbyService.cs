using System.Data;
using System.Text.Json;
using Dapper;
using Quesshi.Engine.Contracts;
using Quesshi.Plugin.Api;
using Quesshi.Shared;

namespace Quesshi.Application.Sessions;

/// <summary>Durable lobby membership and rule changes. Calls are serialized by the session grain.</summary>
public sealed class SessionLobbyService(ITenantDb database, IFormatRegistry formats, ISourceRegistry sources, SessionSnapshotFactory snapshots, IKindRegistry kinds, IClock clock)
{
    public Task<LobbyOutcome> JoinAsync(Guid tenantId, Guid sessionId, Guid playerId, CancellationToken cancellationToken = default) =>
        database.InTenantAsync(tenantId, tx => JoinAsync(tx, sessionId, playerId, cancellationToken), IsolationLevel.ReadCommitted, cancellationToken);

    public Task<LobbyOutcome> LeaveAsync(Guid tenantId, Guid sessionId, Guid playerId, CancellationToken cancellationToken = default) =>
        database.InTenantAsync(tenantId, tx => LeaveAsync(tx, sessionId, playerId, cancellationToken), IsolationLevel.ReadCommitted, cancellationToken);

    public Task<LobbyOutcome> ConfigureAsync(Guid tenantId, Guid sessionId, Guid playerId, string? rulesJson, string? contentJson, int? capacity, CancellationToken cancellationToken = default) =>
        database.InTenantAsync(tenantId, tx => ConfigureAsync(tx, sessionId, playerId, rulesJson, contentJson, capacity, cancellationToken), IsolationLevel.ReadCommitted, cancellationToken);

    public async Task<LobbyOutcome> StartAsync(Guid tenantId, Guid sessionId, Guid playerId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await database.InTenantAsync(tenantId, tx => StartAsync(tx, sessionId, playerId, cancellationToken), IsolationLevel.ReadCommitted, cancellationToken);
        }
        catch (NotEnoughQuestionsException) { return LobbyOutcome.Error("not_enough_questions"); }
        catch (SourceUnavailableException) { return LobbyOutcome.Error("source_unavailable"); }
        catch (InvalidSourceResultException) { return LobbyOutcome.Error("source_unavailable"); }
    }

    private async Task<LobbyOutcome> JoinAsync(ITenantTx tx, Guid sessionId, Guid playerId, CancellationToken cancellationToken)
    {
        var session = await LoadAsync(tx, sessionId, cancellationToken);
        if (session is null) return LobbyOutcome.Error("not_found");
        if (session.Status == "cancelled") return LobbyOutcome.Error("cancelled");
        if (session.Status is "finished") return LobbyOutcome.Error("terminal");
        if (session.DueAt is { } dueAt && dueAt <= clock.Now) return LobbyOutcome.Error("expired");
        if (!formats.TryGet(session.Format ?? "", out var format) || format is null) return LobbyOutcome.Error("not_found");
        if (session.Status != "lobby" && !string.Equals(session.CreationShape, "open", StringComparison.Ordinal))
            return LobbyOutcome.Error("late_join_not_allowed");

        var player = await tx.QuerySingleOrDefaultAsync<PlayerRow>("""
            select id as Id, account_id is null as IsGuest
            from players where id = @playerId and banned_at is null and deleted_at is null
            """, new { playerId }, cancellationToken);
        if (player is null) return LobbyOutcome.Error("not_found");
        if (player.IsGuest && !format.Spec.Guests) return LobbyOutcome.Error("guest_not_allowed");
        var existing = await tx.QuerySingleOrDefaultAsync<ParticipantRow>("""
            select seat as Seat, status as Status from session_participants where session_id = @sessionId and player_id = @playerId
            """, new { sessionId, playerId }, cancellationToken);
        if (existing?.Status == "active") return new LobbyOutcome("joined", session.Version);
        var activePlayers = await tx.QuerySingleOrDefaultAsync<int>("""
            select count(*) from session_participants where session_id = @sessionId and status = 'active' and role = 'player'
            """, new { sessionId }, cancellationToken);
        if (activePlayers >= session.Capacity) return LobbyOutcome.Error("session_full");
        if (existing is not null)
        {
            await tx.ExecuteAsync("""
                update session_participants set status = 'active', eligible = @eligible where session_id = @sessionId and player_id = @playerId
                """, new { sessionId, playerId, eligible = session.Status == "running" && format.Spec.Pacing == Pacing.Shared && !player.IsGuest }, cancellationToken);
        }
        else
        {
            var seat = await tx.QuerySingleOrDefaultAsync<int>("select coalesce(max(seat) + 1, 0) from session_participants where session_id = @sessionId", new { sessionId }, cancellationToken);
            await tx.ExecuteAsync("""
                insert into session_participants (tenant_id, session_id, player_id, seat, role, eligible, phase, slot, step)
                values (app_tenant(), @sessionId, @playerId, @seat, 'player', @eligible, 'idle', -1, 0)
                """, new { sessionId, playerId, seat, eligible = session.Status == "running" && format.Spec.Pacing == Pacing.Shared && !player.IsGuest }, cancellationToken);
        }
        var version = await BumpAsync(tx, sessionId, session.Version, cancellationToken);
        return version is null ? LobbyOutcome.Error("stale_activation") : new LobbyOutcome("joined", version);
    }

    private async Task<LobbyOutcome> LeaveAsync(ITenantTx tx, Guid sessionId, Guid playerId, CancellationToken cancellationToken)
    {
        var session = await LoadAsync(tx, sessionId, cancellationToken);
        if (session is null) return LobbyOutcome.Error("not_found");
        if (session.Status == "cancelled") return LobbyOutcome.Error("cancelled");
        if (session.Status != "lobby") return LobbyOutcome.Error("not_in_lobby");
        if (session.OwnerId == playerId) return LobbyOutcome.Error("owner_must_cancel");
        var changed = await tx.ExecuteAsync("""
            update session_participants set status = 'left', eligible = false, ranked = false
            where session_id = @sessionId and player_id = @playerId and status = 'active'
            """, new { sessionId, playerId }, cancellationToken);
        if (changed == 0) return LobbyOutcome.Error("absent_seat");
        var version = await BumpAsync(tx, sessionId, session.Version, cancellationToken);
        return version is null ? LobbyOutcome.Error("stale_activation") : new LobbyOutcome("left", version);
    }

    private async Task<LobbyOutcome> ConfigureAsync(ITenantTx tx, Guid sessionId, Guid playerId, string? rulesJson, string? contentJson, int? capacity, CancellationToken cancellationToken)
    {
        var session = await LoadAsync(tx, sessionId, cancellationToken);
        if (session is null) return LobbyOutcome.Error("not_found");
        if (session.Status == "cancelled") return LobbyOutcome.Error("cancelled");
        if (session.Status != "lobby") return LobbyOutcome.Error("not_in_lobby");
        if (session.OwnerId != playerId) return LobbyOutcome.Error("not_owner");
        if (!formats.TryGet(session.Format ?? "", out var format) || format is null) return LobbyOutcome.Error("format_unavailable");
        JsonElement rules;
        SessionContent content;
        try
        {
            rules = format.ReadRules(rulesJson is null ? Json(session.Rules) : Json(rulesJson));
            content = contentJson is null ? JsonSerializer.Deserialize(session.Content, SessionJsonContext.Default.SessionContent)!
                : JsonSerializer.Deserialize(contentJson, SessionJsonContext.Default.SessionContent)!;
            var plan = format.Plan(new ContentSpec(content.Source, content.Parameters, content.Count), rules);
            if (plan.Count != content.Count) return LobbyOutcome.Error("invalid_rules");
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidOperationException)
        {
            return LobbyOutcome.Error("invalid_rules");
        }
        if (!sources.TryGet(content.Source, out var source) || source is null
            || !await EnabledAsync(tx, content.Source, cancellationToken)
            || !await source.AuthorizeAsync(tx, content.Parameters, playerId, cancellationToken))
            return LobbyOutcome.Error("source_unavailable");
        var requestedCapacity = capacity ?? session.Capacity;
        var tenantCapacity = Math.Clamp(await tx.QuerySingleOrDefaultAsync<int?>("""
            select coalesce((select (settings ->> 'capacity')::int from tenant_capabilities where type = 'switch' and key = 'sessions'), 20)
            """, cancellationToken: cancellationToken) ?? 20, 1, 500);
        var activePlayers = await tx.QuerySingleOrDefaultAsync<int>("select count(*) from session_participants where session_id = @sessionId and status = 'active' and role = 'player'", new { sessionId }, cancellationToken);
        if (requestedCapacity < activePlayers || requestedCapacity < format.Spec.MinPlayers || requestedCapacity > format.Spec.MaxPlayers || requestedCapacity > tenantCapacity)
            return LobbyOutcome.Error("invalid_capacity");
        var version = await tx.QuerySingleOrDefaultAsync<long?>("""
            update sessions set rules = @rules, content = @content, capacity = @requestedCapacity, version = version + 1
            where id = @sessionId and version = @version returning version
            """, new { sessionId, version = session.Version, rules = new Jsonb<JsonElement>(rules, SessionJsonContext.Default.JsonElement), content = new Jsonb<SessionContent>(content, SessionJsonContext.Default.SessionContent), requestedCapacity }, cancellationToken);
        return version is null ? LobbyOutcome.Error("stale_activation") : new LobbyOutcome("configured", version);
    }

    private async Task<LobbyOutcome> StartAsync(ITenantTx tx, Guid sessionId, Guid playerId, CancellationToken cancellationToken)
    {
        var session = await LoadAsync(tx, sessionId, cancellationToken);
        if (session is null) return LobbyOutcome.Error("not_found");
        if (session.Status == "cancelled") return LobbyOutcome.Error("cancelled");
        if (session.Status != "lobby") return LobbyOutcome.Error("not_in_lobby");
        if (session.Owner != playerId.ToString()) return LobbyOutcome.Error("not_owner");
        if (!formats.TryGet(session.Format ?? "", out var format) || format is null) return LobbyOutcome.Error("format_unavailable");
        var seats = (await tx.QueryAsync<StartSeat>("""
            select p.player_id::text as Player, p.role as Role, p.seat as Number, players.account_id is null as IsGuest
            from session_participants p join players on players.id = p.player_id and players.tenant_id = p.tenant_id
            where p.session_id = @sessionId and p.status = 'active' order by p.seat
            """, new { sessionId }, cancellationToken)).Select(seat => new Seat(seat.Player, seat.Role, Number: seat.Number, IsGuest: seat.IsGuest)).ToArray();
        var playing = seats.Count(seat => format.Spec.Roles.Any(role => role.Id == seat.Role && role.Plays));
        if (playing < format.Spec.MinPlayers) return LobbyOutcome.Error("not_enough_players");
        var content = JsonSerializer.Deserialize(session.Content!.Value.GetRawText(), SessionJsonContext.Default.SessionContent);
        if (content is null) return LobbyOutcome.Error("source_unavailable");
        JsonElement rules;
        DrawPlan plan;
        try { rules = format.ReadRules(session.Rules!.Value); plan = format.Plan(new ContentSpec(content.Source, content.Parameters, content.Count), rules); }
        catch (ArgumentException) { return LobbyOutcome.Error("invalid_rules"); }
        if (!sources.TryGet(content.Source, out var source) || source is null || !await EnabledAsync(tx, content.Source, cancellationToken)) return LobbyOutcome.Error("source_unavailable");
        if (!await source.AuthorizeAsync(tx, content.Parameters, playerId, cancellationToken)) return LobbyOutcome.Error("source_unavailable");
        var sourcePlan = snapshots.Plan(plan, session.Language ?? "en", sessionId);
        var items = await snapshots.DrawAsync(tx, sessionId, source, content.Parameters, sourcePlan, playerId, seats, cancellationToken);
        var now = clock.Now;
        var startSession = session with { StartedAt = now, Status = "running", DueAt = null };
        var initial = SessionState.Create(startSession, Empty, seats, items.Select(item => item.Value), plan.Count);
        var transition = new SessionTransition(initial, kinds, clock, format.Spec.Grace, format.Spec.Scored);
        format.Start(transition);
        var current = transition.Current;
        var version = await tx.QuerySingleOrDefaultAsync<long?>("""
            update sessions set status = @status, started_at = @startedAt, due_at = @dueAt, phase = @phase, slot = @slot, step = @step,
                phase_started_at = @phaseStartedAt, state = @state, state_version = @stateVersion, version = version + 1
            where id = @sessionId and version = @expectedVersion returning version
            """, new { sessionId, expectedVersion = session.Version, status = current.Session.Status, startedAt = current.Session.StartedAt,
                dueAt = current.Session.DueAt, phase = current.Session.Position?.Phase.ToString() ?? "idle", slot = current.Session.Position?.Slot ?? -1,
                step = current.Session.Position?.Step ?? 0, phaseStartedAt = current.Session.PositionStartedAt,
                state = new Jsonb<JsonElement>(current.FormatState, SessionJsonContext.Default.JsonElement), stateVersion = format.Versions.State }, cancellationToken);
        if (version is null) return LobbyOutcome.Error("stale_activation");
        await SessionSnapshotFactory.PersistAsync(tx, sessionId, items, cancellationToken);
        foreach (var seat in current.Seats.Values)
            await tx.ExecuteAsync("""
                update session_participants set eligible = @eligible where session_id = @sessionId and player_id = @playerId
                """, new { sessionId, playerId = Guid.Parse(seat.Player), eligible = format.Spec.Pacing == Pacing.Shared && !seat.IsGuest }, cancellationToken);
        return new LobbyOutcome("started", version);
    }

    public async Task<LobbyOutcome> CancelAsync(Guid tenantId, Guid sessionId, Guid playerId, SessionStateStore store, CancellationToken cancellationToken = default)
    {
        var session = await LoadAsync(tenantId, sessionId, cancellationToken);
        if (session is null) return LobbyOutcome.Error("not_found");
        if (session.Status == "cancelled") return LobbyOutcome.Error("cancelled");
        if (session.OwnerId != playerId) return LobbyOutcome.Error("not_owner");
        await store.EndCoreAsync(tenantId, sessionId, CoreEndReason.Owner, cancellationToken);
        return new LobbyOutcome("cancelled");
    }

    private Task<SessionRow?> LoadAsync(Guid tenantId, Guid sessionId, CancellationToken cancellationToken) =>
        database.InTenantAsync(tenantId, tx => LoadAsync(tx, sessionId, cancellationToken), IsolationLevel.ReadCommitted, cancellationToken, readOnly: true);

    private static async Task<SessionRow?> LoadAsync(ITenantTx tx, Guid sessionId, CancellationToken cancellationToken)
    {
        var row = await tx.QuerySingleOrDefaultAsync<SessionData>("""
            select id as Id, owner_id as OwnerId, format as Format, status as Status, due_at as DueAt, version as Version,
                   capacity as Capacity, rules::text as Rules, content::text as Content, creation_shape as CreationShape
            from sessions where id = @sessionId
            """, new { sessionId }, cancellationToken);
        return row is null ? null : new SessionRow(row.Id.ToString(), null, row.DueAt, null, Status: row.Status, Format: row.Format,
            Version: row.Version, Content: Json(row.Content), Rules: Json(row.Rules), Owner: row.OwnerId.ToString(), Capacity: row.Capacity, CreationShape: row.CreationShape);
    }

    private static Task<long?> BumpAsync(ITenantTx tx, Guid sessionId, long version, CancellationToken cancellationToken) =>
        tx.QuerySingleOrDefaultAsync<long?>("update sessions set version = version + 1 where id = @sessionId and version = @version returning version", new { sessionId, version }, cancellationToken);

    private static async Task<bool> EnabledAsync(ITenantTx tx, string source, CancellationToken cancellationToken) =>
        await tx.QuerySingleOrDefaultAsync<bool?>("select entitled and enabled from tenant_capabilities where type = 'source' and key = @source", new { source }, cancellationToken) == true;
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    private sealed class SessionData
    {
        public Guid Id { get; init; }
        public Guid OwnerId { get; init; }
        public string Format { get; init; } = "";
        public string Status { get; init; } = "";
        public DateTimeOffset? DueAt { get; init; }
        public long Version { get; init; }
        public int Capacity { get; init; }
        public string Rules { get; init; } = "{}";
        public string Content { get; init; } = "{}";
        public string CreationShape { get; init; } = "";
    }
    private sealed class PlayerRow { public Guid Id { get; init; } public bool IsGuest { get; init; } }
    private sealed class ParticipantRow { public int Seat { get; init; } public string Status { get; init; } = ""; }
    private sealed class StartSeat { public string Player { get; init; } = ""; public string Role { get; init; } = ""; public int Number { get; init; } public bool IsGuest { get; init; } }
    private static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement.Clone();
}

public sealed record LobbyOutcome(string Code, long? Version = null)
{
    public static LobbyOutcome Error(string code) => new(code);
}
