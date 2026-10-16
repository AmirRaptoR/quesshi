using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using Quesshi.Plugin.Api;
using Quesshi.Shared;

namespace Quesshi.Application.Content;

internal sealed class QuestionService(IKindRegistry kinds) : IContent
{
    private const int MaximumRenderedBytes = 16 * 1024;

    public async Task<QuestionEnvelope?> GetAsync(ITenantTx transaction, Guid id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var row = await transaction.QuerySingleOrDefaultAsync<Row>(Select + " where id = @id", new { id }, cancellationToken);
        return row is null ? null : ToEnvelope(row);
    }

    public async Task<IReadOnlyList<QuestionEnvelope>> ListAsync(ITenantTx transaction, QuestionFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction); ArgumentNullException.ThrowIfNull(filter);
        var rows = await transaction.QueryAsync<Row>(Select + " " + """
            where (@language is null or lang = @language) and (@family is null or family = @family)
              and (@kind is null or kind = @kind) and (@categoryId is null or category_id = @categoryId)
              and (@level is null or level = @level) and (@status is null or status = @status)
              and (@source is null or source = @source)
              and (@prompt is null or prompt ilike '%' || @prompt || '%')
            order by updated_at desc, id
            """, new { language = filter.Language, family = filter.Family, kind = filter.Kind, filter.CategoryId, filter.Level,
                status = filter.Status is null ? null : StatusText(filter.Status.Value), source = filter.Source is null ? null : SourceText(filter.Source.Value), prompt = filter.Prompt }, cancellationToken);
        return rows.Select(ToEnvelope).ToArray();
    }

    public async Task<QuestionWriteResult> CreateAsync(ITenantTx transaction, QuestionWrite request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var prepared = Prepare(request);
        if (prepared.Result is not null) return prepared.Result;
        if (await IsPromptDuplicateAsync(transaction, prepared.Value!, null, cancellationToken)) return QuestionWriteResult.Duplicate;
        try
        {
            var id = Guid.NewGuid();
            var row = await transaction.QuerySingleOrDefaultAsync<Row>(Insert + " returning " + Returning, Parameters(prepared.Value!, id), cancellationToken);
            return row is null ? QuestionWriteResult.Missing : QuestionWriteResult.Success(ToEnvelope(row));
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return QuestionWriteResult.Duplicate;
        }
    }

    public async Task<QuestionWriteResult> EditAsync(ITenantTx transaction, Guid id, QuestionWrite request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var prepared = Prepare(request);
        if (prepared.Result is not null) return prepared.Result;
        if (await IsPromptDuplicateAsync(transaction, prepared.Value!, id, cancellationToken)) return QuestionWriteResult.Duplicate;
        try
        {
            var row = await transaction.QuerySingleOrDefaultAsync<Row>("""
                update questions set kind = @kind, kind_version = @kindVersion, family = @family, lang = @language,
                    category_id = @categoryId, level = @level, prompt = @prompt, payload = @payload,
                    explanation = @explanation, topic = @topic, fingerprint = @fingerprint, status = @status,
                    source = @source, owner_id = @ownerId, updated_at = now()
                where id = @id returning
                """ + " " + Returning, Parameters(prepared.Value!, id), cancellationToken);
            return row is null ? QuestionWriteResult.Missing : QuestionWriteResult.Success(ToEnvelope(row));
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return QuestionWriteResult.Duplicate;
        }
    }

    public async Task<bool> DeleteAsync(ITenantTx transaction, Guid id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return await transaction.ExecuteAsync("delete from questions where id = @id", new { id }, cancellationToken) > 0;
    }

    public async Task<bool> SetStatusAsync(ITenantTx transaction, Guid id, QuestionStatus status, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return await transaction.ExecuteAsync("update questions set status = @status, updated_at = now() where id = @id", new { id, status = StatusText(status) }, cancellationToken) > 0;
    }

    public async Task<IReadOnlyList<QuestionBucketCount>> BucketCountsAsync(ITenantTx transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var rows = await transaction.QueryAsync<BucketRow>("""
            select lang as Language, category_id as CategoryId, level as Level, kind as Kind,
              count(*) filter (where status = 'approved') as Approved,
              count(*) filter (where status = 'pending') as Pending
            from questions where owner_id is null group by lang, category_id, level, kind
            order by lang, category_id, level, kind
            """, cancellationToken: cancellationToken);
        return rows.Select(row => new QuestionBucketCount(row.Language, row.CategoryId, row.Level, row.Kind, row.Approved, row.Pending)).ToArray();
    }

    private Prepared Prepare(QuestionWrite request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Kind) || string.IsNullOrWhiteSpace(request.Family)
            || string.IsNullOrWhiteSpace(request.Language) || string.IsNullOrWhiteSpace(request.Prompt) || request.KindVersion <= 0
            || request.Level is < 1 or > 5 || (request.Source == QuestionSource.Player) != (request.OwnerId is not null))
            return new(QuestionWriteResult.Validation(new ContentProblem("question.invalid", "The question envelope is invalid.")), null);
        IKindRuntime kind;
        try { kind = kinds.GetCurrent(request.Kind); }
        catch (KeyNotFoundException) { return new(QuestionWriteResult.Validation(new ContentProblem("kind.unknown", "The question kind is not registered.")), null); }
        if (!string.Equals(kind.Family, request.Family, StringComparison.Ordinal)) return new(QuestionWriteResult.Validation(new ContentProblem("family.invalid", "The family does not match the registered kind.")), null);
        JsonElement payload;
        IReadOnlyList<Problem> validation;
        try
        {
            if (request.Payload.ValueKind != JsonValueKind.Object) throw new JsonException("The payload must be an object.");
            payload = kind.Upgrade(request.KindVersion, request.Payload);
            validation = kind.Validate(payload, new ValidateEnv(request.Language));
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return new(QuestionWriteResult.Validation(new ContentProblem("payload.invalid", exception.Message)), null);
        }
        if (validation.Count > 0) return new(QuestionWriteResult.Validation([.. validation.Select(problem => new ContentProblem(problem.Code, problem.Message))]), null);
        try
        {
            var resolved = kind.Resolve(payload, new ResolveContext("content-size-check", 0, []));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(kind.Card(payload, resolved)).Length + JsonSerializer.SerializeToUtf8Bytes(kind.Reveal(payload, resolved)).Length;
            if (bytes > MaximumRenderedBytes) return new(QuestionWriteResult.Oversize, null);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return new(QuestionWriteResult.Validation(new ContentProblem("payload.invalid", exception.Message)), null);
        }
        var normalized = new PreparedWrite(request.Kind.Trim(), kind.Version, kind.Family, request.Language.Trim(), request.CategoryId, request.Level,
            request.Prompt.Trim(), payload.Clone(), request.Explanation?.Trim(), request.Topic?.Trim(), PromptFingerprint.Normalise(request.Prompt), request.Status, request.Source, request.OwnerId);
        return new(null, normalized);
    }

    private static async Task<bool> IsPromptDuplicateAsync(ITenantTx transaction, PreparedWrite write, Guid? exceptId, CancellationToken cancellationToken)
    {
        if (write.Source == QuestionSource.Player) return false;
        var prompts = await transaction.QueryAsync<string>("""
            select prompt from questions where owner_id is null and lang = @language and family = @family
              and (@exceptId is null or id <> @exceptId)
            """, new { write.Language, write.Family, exceptId }, cancellationToken);
        return prompts.Any(prompt => PromptFingerprint.AreDuplicates(write.Prompt, prompt));
    }

    private static object Parameters(PreparedWrite write, Guid id) => new { id, kind = write.Kind, kindVersion = write.KindVersion, family = write.Family, language = write.Language, write.CategoryId, write.Level, write.Prompt,
        payload = new Jsonb<JsonElement>(write.Payload, ContentJsonContext.Default.JsonElement), write.Explanation, write.Topic, write.Fingerprint, status = StatusText(write.Status), source = SourceText(write.Source), write.OwnerId };
    private static string StatusText(QuestionStatus status) => status.ToString().ToLowerInvariant();
    private static string SourceText(QuestionSource source) => source switch { QuestionSource.Ai => "ai", _ => source.ToString().ToLowerInvariant() };
    private static QuestionStatus Status(string value) => Enum.Parse<QuestionStatus>(value, true);
    private static QuestionSource Source(string value) => value == "ai" ? QuestionSource.Ai : Enum.Parse<QuestionSource>(value, true);
    private static QuestionEnvelope ToEnvelope(Row row) => new(row.Id, row.Kind, row.KindVersion, row.Family, row.Language, row.CategoryId, row.Level, row.Prompt,
        JsonDocument.Parse(row.Payload).RootElement.Clone(), row.Explanation, row.Topic, row.Fingerprint, Status(row.Status), Source(row.Source), row.OwnerId,
        new DateTimeOffset(row.CreatedAt), new DateTimeOffset(row.UpdatedAt));

    private const string Returning = "id as Id, kind as Kind, kind_version as KindVersion, family as Family, lang as Language, category_id as CategoryId, level as Level, prompt as Prompt, payload::text as Payload, explanation as Explanation, topic as Topic, fingerprint as Fingerprint, status as Status, source as Source, owner_id as OwnerId, created_at as CreatedAt, updated_at as UpdatedAt";
    private const string Select = "select " + Returning + " from questions";
    private const string Insert = "insert into questions (tenant_id, id, kind, kind_version, family, lang, category_id, level, prompt, payload, explanation, topic, fingerprint, status, source, owner_id) values (current_setting('app.tenant_id')::uuid, @id, @kind, @kindVersion, @family, @language, @categoryId, @level, @prompt, @payload, @explanation, @topic, @fingerprint, @status, @source, @ownerId)";
    private sealed record Prepared(QuestionWriteResult? Result, PreparedWrite? Value);
    private sealed record PreparedWrite(string Kind, int KindVersion, string Family, string Language, Guid? CategoryId, int? Level, string Prompt, JsonElement Payload, string? Explanation, string? Topic, string Fingerprint, QuestionStatus Status, QuestionSource Source, Guid? OwnerId);
    private sealed record Row(Guid Id, string Kind, short KindVersion, string Family, string Language, Guid? CategoryId, short? Level, string Prompt, string Payload, string? Explanation, string? Topic, string? Fingerprint, string Status, string Source, Guid? OwnerId, DateTime CreatedAt, DateTime UpdatedAt);
    private sealed class BucketRow
    {
        public string Language { get; init; } = string.Empty;
        public Guid? CategoryId { get; init; }
        public short? Level { get; init; }
        public string Kind { get; init; } = string.Empty;
        public long Approved { get; init; }
        public long Pending { get; init; }
    }
}

[JsonSerializable(typeof(JsonElement))]
internal sealed partial class ContentJsonContext : JsonSerializerContext;
