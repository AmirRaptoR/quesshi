using System.Text.Json;
using System.Text.Json.Serialization;
using Quesshi.Application.Content;
using Quesshi.Engine.Contracts;
using Quesshi.Plugin.Api;
using Quesshi.Shared;

namespace Quesshi.Application.Sessions;

/// <summary>Creates an idempotent session and its initial durable snapshot in one tenant transaction.</summary>
internal sealed class SessionCreationService(
    IFormatRegistry formats,
    IKindRegistry kinds,
    ISourceRegistry sources,
    SourceResultValidator sourceResults,
    ILocks locks,
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

        await locks.PlayersAsync(transaction, roster.Select(x => x.PlayerId), cancellationToken);
        var players = (await transaction.QueryAsync<PlayerData>("""
            select id as Id, lang as Language, account_id is null as IsGuest
            from players where id = any(@ids) and banned_at is null and deleted_at is null
            """, new { ids = roster.Select(x => x.PlayerId).ToArray() }, cancellationToken)).ToDictionary(x => x.Id);
        if (players.Count != roster.Count || !players.TryGetValue(creatorId, out var creator)) return Invalid("invalid_player");
        if (!format.Spec.Guests && players.Values.Any(player => player.IsGuest)) return Invalid("guest_not_allowed");
        var now = clock.Now;
        var seatValues = roster.Select((participant, number) => new Seat(
            participant.PlayerId.ToString(), participant.Role, Number: number, IsGuest: players[participant.PlayerId].IsGuest)).ToArray();
        var items = Array.Empty<PersistedItem>();

        try
        {
            if (request.Shape != SessionShape.Lobby)
            {
                if (!sources.TryGet(request.Content.Source, out var source)) return new(SessionCreateStatus.SourceUnavailable, Error: "source_unavailable");
                if (!await EnabledAsync(transaction, "source", request.Content.Source, cancellationToken))
                    return new(SessionCreateStatus.SourceUnavailable, Error: "source_unavailable");
                if (!await source!.AuthorizeAsync(transaction, request.Content.Parameters, creatorId, cancellationToken))
                    return new(SessionCreateStatus.SourceUnavailable, Error: "source_unavailable");
                var sourcePlan = new SourceDrawPlan(plan.Count, creator.Language,
                    plan.AcceptedKinds.Select(id => new SourceKind(id, kinds.GetCurrent(id).Family)).ToArray(), plan.Levels, Seed(request.Id));
                var picked = await source.PickAsync(transaction, request.Content.Parameters, sourcePlan, creatorId, cancellationToken);
                if (picked is PickedQuestionIds ids) await locks.ContentAsync(transaction, ids.Ids, cancellationToken);
                var questions = await sourceResults.ValidateAsync(transaction, picked, sourcePlan, cancellationToken);
                items = await CreateItemsAsync(transaction, request.Id, questions, seatValues, cancellationToken);
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

            var created = await InsertWithCodeAsync(transaction, creatorId, request, creator.Language, now, cancellationToken);
            if (created is null)
            {
                var afterCollision = await ReplayAsync(transaction, creatorId, request, cancellationToken);
                return afterCollision ?? new(SessionCreateStatus.IdConflict, Error: "id_conflict");
            }
            state = state with { Session = state.Session with { Code = created } };
            await PersistSeatsAsync(transaction, request.Id, state.Seats.Values, cancellationToken);
            await PersistItemsAsync(transaction, request.Id, items, cancellationToken);
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
        var equal = await transaction.QuerySingleOrDefaultAsync<ReplayRow>("""
            select code as Code, status as Status,
                   (select count(*)::int from session_participants where session_id = sessions.id) as SeatCount
            from sessions
            where id = @id and owner_id = @creatorId and format = @format and capacity = @capacity
              and content = @content and rules = @rules
            """, new { id = request.Id, creatorId, format = request.Format, capacity = request.Capacity,
                content = Json(request.Content), rules = new Jsonb<JsonElement>(request.Rules, SessionJsonContext.Default.JsonElement) }, cancellationToken);
        if (equal is not null && (request.Shape == SessionShape.Lobby ? equal.Status == "lobby" : equal.Status == "running"))
        {
            var expected = Roster(creatorId, request);
            if (expected is not null && equal.SeatCount == expected.Count)
            {
                var seats = await transaction.QueryAsync<ReplaySeat>("""
                    select player_id as PlayerId, role as Role
                    from session_participants where session_id = @id order by seat
                    """, new { id = request.Id }, cancellationToken);
                if (seats.Select(seat => new SessionParticipant(seat.PlayerId, seat.Role)).SequenceEqual(expected))
                    return new(SessionCreateStatus.Replayed, request.Id, equal.Code);
            }
        }
        var exists = await transaction.QuerySingleOrDefaultAsync<Guid?>("select id from sessions where id = @id", new { id = request.Id }, cancellationToken);
        return exists is null ? null : new(SessionCreateStatus.IdConflict, Error: "id_conflict");
    }

    private static IReadOnlyList<SessionParticipant>? Roster(Guid creator, SessionCreateRequest request) => request.Shape switch
    {
        SessionShape.Lobby or SessionShape.Open => [new SessionParticipant(creator)],
        SessionShape.Started when request.Participants is { Count: > 0 } && request.Participants.Any(x => x.PlayerId == creator) => request.Participants,
        _ => null,
    };

    private async Task<string?> InsertWithCodeAsync(ITenantTx transaction, Guid creatorId, SessionCreateRequest request, string language, DateTimeOffset now, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt != 32; attempt++)
        {
            var code = codes.Next();
            var inserted = await transaction.QuerySingleOrDefaultAsync<Guid?>("""
                insert into sessions (tenant_id, id, code, format, owner_id, lang, status, capacity, content, rules, created_at, started_at)
                values (app_tenant(), @id, @code, @format, @creatorId, @language, @status, @capacity, @content, @rules, @now, @startedAt)
                on conflict do nothing returning id
                """, new { id = request.Id, code, format = request.Format, creatorId, language,
                    status = request.Shape == SessionShape.Lobby ? "lobby" : "running", capacity = request.Capacity,
                    content = Json(request.Content), rules = new Jsonb<JsonElement>(request.Rules, SessionJsonContext.Default.JsonElement), now,
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

    private static async Task PersistItemsAsync(ITenantTx transaction, Guid sessionId, IEnumerable<PersistedItem> items, CancellationToken cancellationToken)
    {
        foreach (var item in items)
            await transaction.ExecuteAsync("""
                insert into session_items (tenant_id, session_id, slot, question_id, kind, kind_version, prompt, payload, resolved, card, reveal, media_id, level, category_id, category)
                values (app_tenant(), @sessionId, @slot, @questionId, @kind, @kindVersion, @prompt, @payload, @resolved, @card, @reveal, @mediaId, @level, @categoryId, @category)
                """, new { sessionId, slot = item.Value.Metadata.Slot, questionId = item.QuestionId, kind = item.Value.Metadata.Kind, kindVersion = item.Value.Metadata.KindVersion,
                    prompt = item.Prompt, payload = new Jsonb<JsonElement>(item.Value.Payload, SessionJsonContext.Default.JsonElement), resolved = new Jsonb<JsonElement>(item.Value.Resolved, SessionJsonContext.Default.JsonElement),
                    card = new Jsonb<JsonElement>(item.Value.Card, SessionJsonContext.Default.JsonElement), reveal = new Jsonb<JsonElement>(item.Value.Reveal, SessionJsonContext.Default.JsonElement),
                    mediaId = item.MediaId, level = item.Value.Metadata.Level, categoryId = item.CategoryId,
                    category = item.Category is { } category
                        ? (object)new Jsonb<JsonElement>(category, SessionJsonContext.Default.JsonElement)
                        : DBNull.Value }, cancellationToken);
    }

    private static Task PersistSessionAsync(ITenantTx transaction, Guid sessionId, SessionState state, FormatVersions versions, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync("""
            update sessions set status = @status, due_at = @dueAt, phase = @phase, slot = @slot, step = @step,
                phase_started_at = @phaseStartedAt, state = @state, state_version = @stateVersion
            where id = @sessionId
            """, new { sessionId, status = state.Session.Status, dueAt = state.Session.DueAt, phase = state.Session.Position?.Phase.ToString() ?? "lobby",
                slot = state.Session.Position?.Slot ?? -1, step = state.Session.Position?.Step ?? 0, phaseStartedAt = state.Session.PositionStartedAt,
                state = new Jsonb<JsonElement>(state.FormatState, SessionJsonContext.Default.JsonElement), stateVersion = versions.State }, cancellationToken);

    private async Task<PersistedItem[]> CreateItemsAsync(ITenantTx transaction, Guid sessionId, IReadOnlyList<SourceQuestion> questions, IReadOnlyList<Seat> seats, CancellationToken cancellationToken)
    {
        var values = new List<PersistedItem>(questions.Count);
        for (var slot = 0; slot < questions.Count; slot++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceItem = questions[slot];
            var question = sourceItem.Question;
            var snapshot = sourceItem.Snapshot;
            var kindId = question?.Kind ?? snapshot!.Kind;
            var kindVersion = question?.KindVersion ?? snapshot!.KindVersion;
            var payloadSource = question?.Payload ?? snapshot!.Payload;
            var level = question?.Level ?? snapshot!.Level;
            var categoryId = question?.CategoryId ?? snapshot!.CategoryId;
            var kind = kinds.GetCurrent(kindId);
            var payload = kind.Upgrade(kindVersion, payloadSource);
            JsonElement resolved;
            JsonElement card;
            JsonElement reveal;
            if (snapshot is null)
            {
                var roster = seats.Select(seat => new RosterSeat(seat.Number, seat.Role, "active", seat.Player)).ToArray();
                resolved = kind.Resolve(payload, new ResolveContext(sessionId.ToString(), slot, roster));
                card = kind.Card(payload, resolved);
                reveal = kind.Reveal(payload, resolved);
            }
            else
            {
                resolved = snapshot.Resolved.Clone();
                var upgraded = kind.UpgradeSnapshot(kindVersion, SnapshotJson(snapshot));
                card = upgraded.GetProperty("card").Clone();
                reveal = upgraded.GetProperty("reveal").Clone();
                _ = kind.Card(payload, resolved);
                _ = kind.Reveal(payload, resolved);
            }
            var category = await CategoryAsync(transaction, categoryId, cancellationToken);
            values.Add(new PersistedItem(SessionItemState.Create(new SessionItem(slot, kind.Id, kind.Version, level, categoryId?.ToString()), payload, resolved, card, reveal), question?.Id, question?.Prompt ?? snapshot!.Prompt, question?.MediaId ?? snapshot!.MediaId, categoryId, category));
        }
        return values.ToArray();
    }

    private static async Task<JsonElement?> CategoryAsync(ITenantTx transaction, Guid? categoryId, CancellationToken cancellationToken)
    {
        if (categoryId is null) return null;
        var category = await transaction.QuerySingleOrDefaultAsync<string>("""
            select jsonb_build_object('id', id, 'slug', slug, 'names', names, 'icon', icon, 'color', color)::text
            from categories where id = @categoryId
            """, new { categoryId }, cancellationToken);
        return category is null ? null : JsonDocument.Parse(category).RootElement.Clone();
    }

    private async Task<int> TenantCapacityAsync(ITenantTx transaction, CancellationToken cancellationToken) => Math.Clamp(
        await transaction.QuerySingleOrDefaultAsync<int?>("""
            select coalesce((select (settings ->> 'capacity')::int from tenant_capabilities
                             where type = 'switch' and key = 'sessions'), 20)
            """, cancellationToken: cancellationToken) ?? 20, 1, 500);
    private static async Task<bool> EnabledAsync(ITenantTx transaction, string type, string key, CancellationToken cancellationToken) =>
        await transaction.QuerySingleOrDefaultAsync<bool?>("select entitled and enabled from tenant_capabilities where type = @type and key = @key", new { type, key }, cancellationToken) == true;
    private static Jsonb<SessionContent> Json(SessionContent content) => new(content, SessionJsonContext.Default.SessionContent);
    private static JsonElement ContentElement(SessionContent content) => JsonSerializer.SerializeToElement(content, SessionJsonContext.Default.SessionContent);
    private static JsonElement SnapshotJson(ReadySnapshot snapshot) => JsonSerializer.SerializeToElement(new
    {
        card = snapshot.Card,
        reveal = snapshot.Reveal,
        answers = Array.Empty<JsonElement>(),
    });
    private static SessionCreateResult Invalid(string error) => new(SessionCreateStatus.Invalid, Error: error);
    private static long Seed(Guid id) => BitConverter.ToInt64(id.ToByteArray());
    private static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement.Clone();
    private sealed record PlayerData(Guid Id, string Language, bool IsGuest);
    private sealed record ReplayRow(string Code, string Status, int SeatCount);
    private sealed record ReplaySeat(Guid PlayerId, string Role);
    private sealed record PersistedItem(SessionItemState Value, Guid? QuestionId, string Prompt, Guid? MediaId, Guid? CategoryId, JsonElement? Category);
}

[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(SessionContent))]
internal sealed partial class SessionJsonContext : JsonSerializerContext;
