using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Quesshi.Plugin.Api;
using Quesshi.Shared;

namespace Quesshi.Application.Content;

/// <summary>Persists and validates versioned question envelopes inside a caller-owned transaction.</summary>
internal sealed class ContentService(IKindRegistry kinds) : IContent
{
    private const int MaximumCardAndRevealBytes = 16 * 1024;

    public async Task<QuestionEnvelope?> GetAsync(ITenantTx transaction, Guid id, CancellationToken cancellationToken = default) =>
        ToEnvelope(await transaction.QuerySingleOrDefaultAsync<PersistedQuestion>(SelectQuestion + " where id = @id", new { id }, cancellationToken));

    public async Task<IReadOnlyList<QuestionEnvelope>> ListAsync(ITenantTx transaction, QuestionFilter? filter = null, CancellationToken cancellationToken = default)
    {
        var rows = await transaction.QueryAsync<PersistedQuestion>($"""
            {SelectQuestion}
            where (@language is null or lang = @language)
              and (@family is null or family = @family)
              and (@kind is null or kind = @kind)
              and (@categoryId is null or category_id = @categoryId)
              and (@level is null or level = @level)
              and (@status is null or status = @status)
              and (@source is null or source = @source)
              and (@prompt is null or strpos(lower(prompt), lower(@prompt)) > 0)
            order by created_at, id
            """, FilterParameters(filter), cancellationToken);
        return rows.Select(question => ToEnvelope(question)!).ToArray();
    }

    public async Task<QuestionWriteResult> CreateAsync(ITenantTx transaction, QuestionWrite request, CancellationToken cancellationToken = default)
    {
        var prepared = Prepare(request);
        if (prepared.Result is not null) return prepared.Result;
        if (!await IsSupportedLanguageAsync(transaction, prepared.Write!.Language, cancellationToken))
            return QuestionWriteResult.Invalid([new("language.unsupported", "The question language is not enabled for this tenant.")]);
        if (await ValidateReferencesAsync(transaction, prepared.Write!, cancellationToken) is { } referenceError)
            return referenceError;

        if (await CollidesAsync(transaction, prepared.Write!, null, cancellationToken)) return QuestionWriteResult.Duplicate;
        if (await TopicCollidesAsync(transaction, prepared.Write!, null, cancellationToken)) return QuestionWriteResult.Duplicate;
        var persisted = await transaction.QuerySingleOrDefaultAsync<PersistedQuestion>($"""
                insert into questions (
                    tenant_id, id, kind, kind_version, family, lang, category_id, level, prompt, payload,
                    explanation, topic, fingerprint, status, source, owner_id)
                values (
                    current_setting('app.tenant_id')::uuid, @id, @kind, @kindVersion, @family, @language, @categoryId, @level, @prompt, @payload,
                    @explanation, @topic, @fingerprint, @status, @source, @ownerId)
                on conflict (tenant_id, lang, family, topic)
                    where topic is not null and owner_id is null do nothing
                returning {QuestionColumns}
                """, Parameters(prepared.Write!, Guid.NewGuid()), cancellationToken);
        return persisted is null ? QuestionWriteResult.Duplicate : new(QuestionWriteStatus.Success, ToEnvelope(persisted));
    }

    public async Task<QuestionWriteResult> EditAsync(ITenantTx transaction, Guid id, QuestionWrite request, CancellationToken cancellationToken = default)
    {
        var prepared = Prepare(request);
        if (prepared.Result is not null) return prepared.Result;
        if (!await IsSupportedLanguageAsync(transaction, prepared.Write!.Language, cancellationToken))
            return QuestionWriteResult.Invalid([new("language.unsupported", "The question language is not enabled for this tenant.")]);
        if (await ValidateReferencesAsync(transaction, prepared.Write!, cancellationToken) is { } referenceError)
            return referenceError;

        if (await CollidesAsync(transaction, prepared.Write!, id, cancellationToken)) return QuestionWriteResult.Duplicate;
        if (await TopicCollidesAsync(transaction, prepared.Write!, id, cancellationToken)) return QuestionWriteResult.Duplicate;
        var persisted = await transaction.QuerySingleOrDefaultAsync<PersistedQuestion>($"""
                update questions
                set kind = @kind, kind_version = @kindVersion, family = @family, lang = @language,
                    category_id = @categoryId, level = @level, prompt = @prompt, payload = @payload,
                    explanation = @explanation, topic = @topic, fingerprint = @fingerprint,
                    status = @status, source = @source, owner_id = @ownerId, updated_at = now()
                where id = @id
                returning {QuestionColumns}
                """, Parameters(prepared.Write!, id), cancellationToken);
        return persisted is null ? QuestionWriteResult.Missing : new(QuestionWriteStatus.Success, ToEnvelope(persisted));
    }

    public async Task<bool> DeleteAsync(ITenantTx transaction, Guid id, CancellationToken cancellationToken = default) =>
        await transaction.ExecuteAsync("delete from questions where id = @id", new { id }, cancellationToken) != 0;

    public async Task<bool> SetStatusAsync(ITenantTx transaction, Guid id, QuestionStatus status, CancellationToken cancellationToken = default) =>
        await transaction.ExecuteAsync("update questions set status = @status, updated_at = now() where id = @id", new { id, status = StatusValue(status) }, cancellationToken) != 0;

    public async Task<IReadOnlyList<QuestionBucketCount>> BucketCountsAsync(ITenantTx transaction, QuestionFilter? filter = null, CancellationToken cancellationToken = default)
    {
        var rows = await transaction.QueryAsync<PersistedBucket>($"""
            select lang as Language, category_id as CategoryId, level::integer as Level, kind as Kind,
                   count(*) filter (where status = 'approved')::integer as Approved,
                   count(*) filter (where status = 'pending')::integer as Pending
            from questions
            where (@language is null or lang = @language)
              and (@family is null or family = @family)
              and (@kind is null or kind = @kind)
              and (@categoryId is null or category_id = @categoryId)
              and (@level is null or level = @level)
              and (@status is null or status = @status)
              and (@source is null or source = @source)
              and (@prompt is null or strpos(lower(prompt), lower(@prompt)) > 0)
            group by lang, category_id, level, kind
            order by lang, category_id, level, kind
            """, FilterParameters(filter), cancellationToken);
        return rows.Select(row => new QuestionBucketCount(row.Language, row.CategoryId, row.Level, row.Kind, row.Approved, row.Pending)).ToArray();
    }

    private PreparedWrite Prepare(QuestionWrite request)
    {
        if (string.IsNullOrWhiteSpace(request.Kind) || string.IsNullOrWhiteSpace(request.Family) || string.IsNullOrWhiteSpace(request.Language)
            || string.IsNullOrWhiteSpace(request.Prompt) || request.Level is < 1 or > 5
            || !Enum.IsDefined(request.Status) || !Enum.IsDefined(request.Source)
            || (request.Source == QuestionSource.Player) != request.OwnerId.HasValue)
            return new(QuestionWriteResult.Invalid([new("question.invalid", "The question envelope is incomplete or inconsistent.")]));

        if (request.Payload.ValueKind != JsonValueKind.Object)
            return new(QuestionWriteResult.Invalid([new("payload.malformed", "The question payload must be a JSON object.")]));

        if (!kinds.TryGetCurrent(request.Kind.Trim(), out var kind) || kind is null)
            return new(QuestionWriteResult.Invalid([new("kind.unknown", "The question kind is not registered.")]));
        if (!string.Equals(kind.Family, request.Family.Trim(), StringComparison.Ordinal))
            return new(QuestionWriteResult.Invalid([new("family.invalid", "The question family does not match its kind.")]));

        JsonElement payload;
        try
        {
            payload = kind.Upgrade(request.KindVersion, request.Payload);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return new(QuestionWriteResult.Invalid([new("payload.malformed", exception.Message)]));
        }

        var problems = kind.Validate(payload, new ValidateEnv(request.Language.Trim()))
            .Select(problem => new ContentProblem(problem.Code, problem.Message)).ToArray();
        if (problems.Length > 0) return new(QuestionWriteResult.Invalid(problems));

        try
        {
            var resolved = kind.Resolve(payload, new ResolveContext("content", 0, []));
            var size = Encoding.UTF8.GetByteCount(kind.Card(payload, resolved).GetRawText())
                + Encoding.UTF8.GetByteCount(kind.Reveal(payload, resolved).GetRawText());
            if (size > MaximumCardAndRevealBytes) return new(QuestionWriteResult.Oversize);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return new(QuestionWriteResult.Invalid([new("payload.malformed", exception.Message)]));
        }

        return new(Write: new NormalizedWrite(
            kind.Id, kind.Version, kind.Family, request.Language.Trim(), request.CategoryId, request.Level,
            request.Prompt.Trim(), payload.Clone(), TrimOrNull(request.Explanation), TrimOrNull(request.Topic),
            PromptFingerprint.Normalise(request.Prompt), request.Status, request.Source, request.OwnerId));
    }

    private static async Task<bool> CollidesAsync(ITenantTx transaction, NormalizedWrite write, Guid? exceptId, CancellationToken cancellationToken)
    {
        if (write.OwnerId.HasValue) return false;
        var existing = await transaction.QueryAsync<string>("""
            select prompt from questions
            where owner_id is null and lang = @language
              and category_id is not distinct from @categoryId
              and (@exceptId is null or id <> @exceptId)
            """, new { write.Language, write.CategoryId, exceptId }, cancellationToken);
        return PromptFingerprint.CollidesWith(write.Prompt, existing);
    }

    private static async Task<bool> TopicCollidesAsync(ITenantTx transaction, NormalizedWrite write, Guid? exceptId, CancellationToken cancellationToken)
    {
        if (write.OwnerId.HasValue || write.Topic is null) return false;

        await transaction.ExecuteAsync("""
            select pg_advisory_xact_lock(hashtextextended(
                'quesshi:question-topic:' || current_setting('app.tenant_id') || ':' || @language || ':' || @family || ':' || @topic,
                0))
            """, new { write.Language, write.Family, write.Topic }, cancellationToken);
        return await transaction.QuerySingleOrDefaultAsync<int?>("""
            select 1 from questions
            where owner_id is null and lang = @language and family = @family and topic = @topic
              and (@exceptId is null or id <> @exceptId)
            limit 1
            """, new { write.Language, write.Family, write.Topic, exceptId }, cancellationToken) is not null;
    }

    private static async Task<bool> IsSupportedLanguageAsync(ITenantTx transaction, string language, CancellationToken cancellationToken) =>
        await transaction.QuerySingleOrDefaultAsync<string>("select code from tenant_languages where code = @language", new { language }, cancellationToken) is not null;

    private static async Task<QuestionWriteResult?> ValidateReferencesAsync(ITenantTx transaction, NormalizedWrite write, CancellationToken cancellationToken)
    {
        if (write.CategoryId.HasValue && await transaction.QuerySingleOrDefaultAsync<Guid?>("select id from categories where id = @id", new { id = write.CategoryId }, cancellationToken) is null)
            return QuestionWriteResult.Invalid([new("category.unknown", "The question category is not available in this tenant.")]);

        if (write.OwnerId.HasValue && await transaction.QuerySingleOrDefaultAsync<Guid?>("select id from players where id = @id", new { id = write.OwnerId }, cancellationToken) is null)
            return QuestionWriteResult.Invalid([new("owner.unknown", "The question owner is not available in this tenant.")]);

        return null;
    }

    private static object Parameters(NormalizedWrite write, Guid id) => new
    {
        id,
        kind = write.Kind,
        kindVersion = write.KindVersion,
        family = write.Family,
        language = write.Language,
        write.CategoryId,
        write.Level,
        prompt = write.Prompt,
        payload = new Jsonb<JsonElement>(write.Payload, ContentJsonContext.Default.JsonElement),
        explanation = write.Explanation,
        topic = write.Topic,
        fingerprint = write.Fingerprint,
        status = StatusValue(write.Status),
        source = SourceValue(write.Source),
        write.OwnerId,
    };

    private static object FilterParameters(QuestionFilter? filter) => new
    {
        language = TrimOrNull(filter?.Language),
        family = TrimOrNull(filter?.Family),
        kind = TrimOrNull(filter?.Kind),
        categoryId = filter?.CategoryId,
        level = filter?.Level,
        status = filter?.Status is { } status ? StatusValue(status) : null,
        source = filter?.Source is { } source ? SourceValue(source) : null,
        prompt = TrimOrNull(filter?.Prompt),
    };

    private static QuestionEnvelope? ToEnvelope(PersistedQuestion? question) => question is null ? null : new(
        question.Id, question.Kind, question.KindVersion, question.Family, question.Language, question.CategoryId, question.Level,
        question.Prompt, JsonDocument.Parse(question.PayloadJson).RootElement.Clone(), question.Explanation, question.Topic,
        question.Fingerprint, ParseStatus(question.Status), ParseSource(question.Source), question.OwnerId);

    private static string? TrimOrNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string StatusValue(QuestionStatus status) => status.ToString().ToLowerInvariant();
    private static string SourceValue(QuestionSource source) => source == QuestionSource.Ai ? "ai" : source.ToString().ToLowerInvariant();
    private static QuestionStatus ParseStatus(string value) => Enum.Parse<QuestionStatus>(value, true);
    private static QuestionSource ParseSource(string value) => Enum.Parse<QuestionSource>(value, true);

    private const string QuestionColumns = "id as Id, kind as Kind, kind_version::integer as KindVersion, family as Family, lang as Language, category_id as CategoryId, level::integer as Level, prompt as Prompt, payload::text as PayloadJson, explanation as Explanation, topic as Topic, fingerprint as Fingerprint, status as Status, source as Source, owner_id as OwnerId";
    private const string SelectQuestion = "select " + QuestionColumns + " from questions";

    private sealed record PreparedWrite(QuestionWriteResult? Result = null, NormalizedWrite? Write = null);
    private sealed record NormalizedWrite(string Kind, int KindVersion, string Family, string Language, Guid? CategoryId, int? Level, string Prompt, JsonElement Payload, string? Explanation, string? Topic, string Fingerprint, QuestionStatus Status, QuestionSource Source, Guid? OwnerId);
    private sealed record PersistedQuestion(Guid Id, string Kind, int KindVersion, string Family, string Language, Guid? CategoryId, int? Level, string Prompt, string PayloadJson, string? Explanation, string? Topic, string Fingerprint, string Status, string Source, Guid? OwnerId);
    private sealed record PersistedBucket(string Language, Guid? CategoryId, int? Level, string Kind, int Approved, int Pending);
}

[JsonSerializable(typeof(JsonElement))]
internal sealed partial class ContentJsonContext : JsonSerializerContext;
