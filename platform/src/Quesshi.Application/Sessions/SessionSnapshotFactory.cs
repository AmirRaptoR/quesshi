using System.Text.Json;
using Dapper;
using Quesshi.Application.Content;
using Quesshi.Engine.Contracts;
using Quesshi.Plugin.Api;
using Quesshi.Shared;

namespace Quesshi.Application.Sessions;

/// <summary>Creates the immutable item snapshots shared by creation and the lobby start transition.</summary>
public sealed class SessionSnapshotFactory(IKindRegistry kinds, SourceResultValidator sourceResults, ILocks locks)
{
    public SourceDrawPlan Plan(DrawPlan plan, string language, Guid sessionId) => new(plan.Count, language,
        plan.AcceptedKinds.Select(id => new SourceKind(id, kinds.GetCurrent(id).Family)).ToArray(), plan.Levels,
        BitConverter.ToInt64(sessionId.ToByteArray()));

    public Task LockPlayersAsync(ITenantTx transaction, IEnumerable<Guid> playerIds, CancellationToken cancellationToken) =>
        locks.PlayersAsync(transaction, playerIds, cancellationToken);

    public async Task<PersistedSessionItem[]> DrawAsync(ITenantTx transaction, Guid sessionId, ISource source, JsonElement parameters,
        SourceDrawPlan plan, Guid actorId, IReadOnlyList<Seat> seats, CancellationToken cancellationToken)
    {
        var picked = await source.PickAsync(transaction, parameters, plan, actorId, cancellationToken);
        if (picked is PickedQuestionIds ids) await locks.ContentAsync(transaction, ids.Ids, cancellationToken);
        var questions = await sourceResults.ValidateAsync(transaction, picked, plan, cancellationToken);
        var values = new List<PersistedSessionItem>(questions.Count);
        for (var slot = 0; slot < questions.Count; slot++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceItem = questions[slot];
            var question = sourceItem.Question;
            var snapshot = sourceItem.Snapshot;
            if (question is null && snapshot is null) throw new InvalidSourceResultException("A source question must contain a question or ready snapshot.");
            var kind = kinds.GetCurrent(question is null ? snapshot!.Kind : question.Kind);
            var version = question is null ? snapshot!.KindVersion : question.KindVersion;
            var payload = kind.Upgrade(version, question is null ? snapshot!.Payload : question.Payload);
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
                var upgraded = kind.UpgradeSnapshot(version, JsonSerializer.SerializeToElement(new { card = snapshot.Card, reveal = snapshot.Reveal, answers = Array.Empty<JsonElement>() }));
                card = upgraded.GetProperty("card").Clone();
                reveal = upgraded.GetProperty("reveal").Clone();
                _ = kind.Card(payload, resolved);
                _ = kind.Reveal(payload, resolved);
            }
            var categoryId = question is null ? snapshot!.CategoryId : question.CategoryId;
            var level = question is null ? snapshot!.Level : question.Level;
            var prompt = question is null ? snapshot!.Prompt : question.Prompt;
            var mediaId = question is null ? snapshot!.MediaId : question.MediaId;
            var category = await CategoryAsync(transaction, categoryId, cancellationToken);
            values.Add(new PersistedSessionItem(SessionItemState.Create(new SessionItem(slot, kind.Id, kind.Version, level, categoryId?.ToString()), payload, resolved, card, reveal),
                question?.Id, prompt, mediaId, categoryId, category));
        }
        return values.ToArray();
    }

    public static async Task PersistAsync(ITenantTx transaction, Guid sessionId, IEnumerable<PersistedSessionItem> items, CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            var parameters = new DynamicParameters();
            parameters.Add("sessionId", sessionId); parameters.Add("slot", item.Value.Metadata.Slot); parameters.Add("questionId", item.QuestionId);
            parameters.Add("kind", item.Value.Metadata.Kind); parameters.Add("kindVersion", item.Value.Metadata.KindVersion); parameters.Add("prompt", item.Prompt);
            parameters.Add("payload", new Jsonb<JsonElement>(item.Value.Payload, SessionJsonContext.Default.JsonElement));
            parameters.Add("resolved", new Jsonb<JsonElement>(item.Value.Resolved, SessionJsonContext.Default.JsonElement));
            parameters.Add("card", new Jsonb<JsonElement>(item.Value.Card, SessionJsonContext.Default.JsonElement));
            parameters.Add("reveal", new Jsonb<JsonElement>(item.Value.Reveal, SessionJsonContext.Default.JsonElement));
            parameters.Add("mediaId", item.MediaId); parameters.Add("level", item.Value.Metadata.Level); parameters.Add("categoryId", item.CategoryId);
            parameters.Add("category", item.Category is { } category ? new Jsonb<JsonElement>(category, SessionJsonContext.Default.JsonElement) : null);
            await transaction.ExecuteAsync("""
                insert into session_items (tenant_id, session_id, slot, question_id, kind, kind_version, prompt, payload, resolved, card, reveal, media_id, level, category_id, category)
                values (app_tenant(), @sessionId, @slot, @questionId, @kind, @kindVersion, @prompt, @payload, @resolved, @card, @reveal, @mediaId, @level, @categoryId, @category)
                """, parameters, cancellationToken);
        }
    }

    private static async Task<JsonElement?> CategoryAsync(ITenantTx transaction, Guid? categoryId, CancellationToken cancellationToken)
    {
        if (categoryId is null) return null;
        var category = await transaction.QuerySingleOrDefaultAsync<string>("""
            select jsonb_build_object('id', id, 'slug', slug, 'names', names, 'icon', icon, 'color', color)::text from categories where id = @categoryId
            """, new { categoryId }, cancellationToken);
        return category is null ? null : JsonDocument.Parse(category).RootElement.Clone();
    }
}

public sealed record PersistedSessionItem(SessionItemState Value, Guid? QuestionId, string Prompt, Guid? MediaId, Guid? CategoryId, JsonElement? Category);
