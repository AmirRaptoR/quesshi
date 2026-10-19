using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Quesshi.Application.Imports;
using Quesshi.Application.Media;
using Quesshi.Kinds;
using Quesshi.Kinds.Choice;
using Quesshi.Kinds.MapPick;
using Quesshi.Kinds.Order;
using Quesshi.Kinds.PollChoice;
using Quesshi.Kinds.PollRoster;
using Quesshi.Plugin.Api;
using Quesshi.Server.Security;
using Quesshi.Server.Tenants;
using Quesshi.Shared;

namespace Quesshi.Server.Api;

/// <summary>Editor import routes for tenant question banks.</summary>
public sealed class ImportEndpoints : ICoreRouteModule
{
    public void Map(CoreRouteBuilder routes)
    {
        var imports = routes.AdminRoutes(AdminRole.Editor).MapGroup("/import");
        imports.MapPost("", ImportAsync).WithMetadata(new AdminAuditMetadata("admin.question.import", "question"));
        imports.MapGet("/templates/{kind}.{format}", TemplateAsync);
    }

    private static async Task<IResult> ImportAsync(
        HttpContext context,
        [FromServices] ITenantDb database,
        [FromServices] IContent content,
        [FromServices] IKindRegistry kinds,
        [FromServices] IMediaFetcher mediaFetcher,
        [FromServices] IMediaStore mediaStore,
        CancellationToken cancellationToken)
    {
        if (!context.Request.HasFormContentType) return Error("invalid_import", StatusCodes.Status400BadRequest);
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        var form = await context.Request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file");
        var bundle = form.Files.GetFile("mediaBundle") ?? form.Files.GetFile("bundle");
        var requestedKind = Value(form, "kind") ?? context.Request.Query["kind"].ToString();
        if (!TryKind(requestedKind, kinds, out var kind, out var mapping)) return Error("unknown_kind", StatusCodes.Status400BadRequest);
        var requestedFormat = Value(form, "format") ?? context.Request.Query["format"].ToString();
        var format = string.IsNullOrWhiteSpace(requestedFormat) ? FileFormat(file?.FileName) : requestedFormat;
        var required = new[] { "prompt" }.Concat(mapping!.CsvColumns).ToArray();
        await using var input = file?.OpenReadStream();
        var parsed = await ImportParsing.ParseAsync(format, input, file?.Length ?? 0, required, cancellationToken);
        if (parsed.Error is not null) return Error(parsed.Error, StatusCodes.Status400BadRequest);
        if (!TryBoolean(Value(form, "dryRun") ?? context.Request.Query["dryRun"].ToString(), true, out var dryRun)) return Error("bad_dry_run", StatusCodes.Status400BadRequest);
        if (!TryStatus(Value(form, "status") ?? context.Request.Query["status"].ToString(), QuestionStatus.Pending, out var defaultStatus)) return Error("bad_status", StatusCodes.Status400BadRequest);

        var state = await database.InTenantAsync(tenant.Id, async transaction => new ImportState(
            await content.ListAsync(transaction, cancellationToken: cancellationToken),
            await transaction.QueryAsync<string>("select code from tenant_languages", cancellationToken: cancellationToken),
            await transaction.QueryAsync<Guid>("select id from categories", cancellationToken: cancellationToken)), cancellationToken: cancellationToken, readOnly: true);

        var reports = new List<ImportRowReport>(parsed.Document!.Rows.Count);
        var accepted = new List<ImportCandidate>();
        foreach (var row in parsed.Document.Rows)
        {
            if (row.IsMalformed)
            {
                reports.Add(new(row.Number, "bad_row", [new("row.malformed", "The row is not a valid CSV or JSON object.")]));
                continue;
            }

            var candidate = BuildCandidate(row, kind!, mapping!, defaultStatus);
            if (candidate.Error is not null)
            {
                reports.Add(new(row.Number, "invalid", [candidate.Error]));
                continue;
            }

            var problems = Validate(candidate.Write!, kind!, state);
            if (problems.Count > 0)
            {
                reports.Add(new(row.Number, "invalid", problems));
                continue;
            }

            var duplicate = FindDuplicate(candidate.Write!, state.Questions, accepted);
            if (duplicate.QuestionId is not null || duplicate.Row is not null)
            {
                reports.Add(new(row.Number, "duplicate", null, duplicate.QuestionId, duplicate.Row));
                continue;
            }

            var media = await ImportMediaAsync(tenant.Id, candidate.Media, bundle, dryRun, mediaFetcher, mediaStore, cancellationToken);
            if (media.Error is not null)
            {
                reports.Add(new(row.Number, media.Error, [new("media.invalid", "The row media could not be reached or read from the bundle.")]));
                continue;
            }
            if (media.AssetId is not null) candidate = candidate with { Write = candidate.Write! with { MediaId = media.AssetId } };

            accepted.Add(new ImportCandidate(row.Number, candidate.Write!, candidate.Media));
        }

        if (dryRun)
        {
            reports.AddRange(accepted.Select(candidate => new ImportRowReport(candidate.Row, "would_import")));
            return Results.Ok(Report(reports, Warnings(state, accepted)));
        }

        foreach (var candidate in accepted)
        {
            var result = await database.InTenantAsync(tenant.Id, transaction => content.CreateAsync(transaction, candidate.Write, cancellationToken), cancellationToken: cancellationToken);
            reports.Add(result.Status switch
            {
                QuestionWriteStatus.Success => new ImportRowReport(candidate.Row, "imported", QuestionId: result.Question!.Id),
                QuestionWriteStatus.Duplicate => DuplicateAfterCommit(candidate, await database.InTenantAsync(tenant.Id, transaction => content.ListAsync(transaction, cancellationToken: cancellationToken), cancellationToken: cancellationToken, readOnly: true)),
                _ => new ImportRowReport(candidate.Row, "invalid", result.Problems ?? [new("question.invalid", "The question could not be imported.")]),
            });
        }
        return Results.Ok(Report(reports, Warnings(state, accepted)));
    }

    private static async Task<IResult> TemplateAsync(
        HttpContext context,
        string kind,
        string format,
        [FromServices] ITenantDb database,
        [FromServices] IKindRegistry kinds,
        CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        if (!TryKind(kind, kinds, out _, out var mapping)) return Error("unknown_kind", StatusCodes.Status400BadRequest);
        format = format.ToLowerInvariant();
        if (format is not ("csv" or "json")) return Error("bad_format", StatusCodes.Status400BadRequest);
        var category = await database.InTenantAsync(tenant.Id,
            transaction => transaction.QuerySingleOrDefaultAsync<Guid?>("select id from categories where is_active order by sort_order, id limit 1", cancellationToken: cancellationToken),
            cancellationToken: cancellationToken,
            readOnly: true);
        if (category is null) return Error("no_category", StatusCodes.Status409Conflict);

        var example = Example(mapping!, format == "csv");
        var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["language"] = "en",
            ["category"] = category.Value.ToString(),
            ["level"] = "2",
            ["prompt"] = "Example question",
            ["explanation"] = "An example imported question.",
            ["status"] = "pending",
        };
        foreach (var (name, value) in example.Fields) fields[name] = value;
        var bytes = format == "csv" ? Csv(fields) : JsonSerializer.SerializeToUtf8Bytes(new[] { fields });
        return Results.File(bytes, format == "csv" ? "text/csv" : "application/json", $"{kind}.{format}");
    }

    private static CandidateBuild BuildCandidate(ImportDocumentRow documentRow, IKindRuntime kind, IImports mapping, QuestionStatus defaultStatus)
    {
        var row = new ImportRow(documentRow.Fields);
        JsonElement payload;
        try
        {
            if (!mapping.Matches(LegacyKind(kind.Id), row)) return new(new ContentProblem("kind.mismatch", "The row does not match the selected question kind."));
            payload = Payload(kind, mapping, row);
        }
        catch (ArgumentException exception)
        {
            return new(new ContentProblem("payload.malformed", exception.Message));
        }
        catch (FormatException exception)
        {
            return new(new ContentProblem("payload.malformed", exception.Message));
        }
        catch (JsonException exception)
        {
            return new(new ContentProblem("payload.malformed", exception.Message));
        }
        var categoryText = Field(row, "category", "categoryId");
        Guid? category = null;
        if (categoryText is not null)
        {
            if (!Guid.TryParse(categoryText, out var parsedCategory)) return new(new ContentProblem("category.invalid", "Category must be a category id."));
            category = parsedCategory;
        }
        var levelText = row.Field("level");
        int? level = null;
        if (levelText is not null)
        {
            if (!int.TryParse(levelText, out var parsedLevel)) return new(new ContentProblem("level.invalid", "Level must be a number from 1 to 5."));
            level = parsedLevel;
        }
        if (!TryStatus(row.Field("status"), defaultStatus, out var status)) return new(new ContentProblem("status.invalid", "Status must be draft, pending, approved, or rejected."));
        return new(new QuestionWrite(kind.Id, kind.Version, kind.Family, Field(row, "language", "lang"), category, level,
            row.Field("prompt"), payload, row.Field("explanation"), TopicKey.From(row.Field("subject"), row.Field("aspect")), status, QuestionSource.Import, null),
            new MediaReference(row.Field("mediaUrl"), row.Field("mediaKind"), row.Field("mediaAttribution")));
    }

    private static async Task<MediaImportResult> ImportMediaAsync(Guid tenantId, MediaReference? reference, IFormFile? bundle, bool dryRun, IMediaFetcher fetcher, IMediaStore store, CancellationToken cancellationToken)
    {
        if (reference is null || string.IsNullOrWhiteSpace(reference.Value)) return new();
        if (Uri.TryCreate(reference.Value, UriKind.Absolute, out var url))
        {
            var fetched = dryRun
                ? await fetcher.ProbeAsync(url, cancellationToken)
                : await fetcher.FetchAsync(tenantId, url, reference.Attribution, cancellationToken);
            if (fetched.Status != MediaFetchStatus.Success) return new("media_unreachable");
            if (reference.Kind is not null
                && (!TryMediaKind(reference.Kind, out var expected)
                    || (fetched.Asset?.Kind ?? fetched.Kind) != expected)) return new("media_invalid");
            return new(null, fetched.Asset?.Id);
        }

        if (!IsBundlePath(reference.Value) || bundle is null) return new("media_bundle_invalid");
        try
        {
            await using var input = bundle.OpenReadStream();
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
            var entry = archive.Entries.SingleOrDefault(item => string.Equals(item.FullName, reference.Value, StringComparison.Ordinal));
            if (entry is null || string.IsNullOrEmpty(entry.Name) || !IsBundlePath(entry.FullName) || entry.Length > 25 * 1024 * 1024) return new("media_bundle_invalid");
            await using var entryStream = entry.Open();
            await using var output = new MemoryStream();
            await entryStream.CopyToAsync(output, cancellationToken);
            var bytes = output.ToArray();
            if (!MediaSniffer.TryInspect(bytes, out var kind, out var mime, out var meta)) return new("media_bundle_invalid");
            if (reference.Kind is not null && (!TryMediaKind(reference.Kind, out var expected) || expected != kind)) return new("media_bundle_invalid");
            if (dryRun) return new();
            var asset = await store.StoreAsync(tenantId, new MediaInput(kind, mime, bytes, meta, reference.Attribution), cancellationToken);
            return new(null, asset.Id);
        }
        catch (InvalidDataException)
        {
            return new("media_bundle_invalid");
        }
    }

    private static IReadOnlyList<ContentProblem> Validate(QuestionWrite write, IKindRuntime kind, ImportState state)
    {
        var problems = new List<ContentProblem>();
        if (string.IsNullOrWhiteSpace(write.Language) || write.CategoryId is null || string.IsNullOrWhiteSpace(write.Prompt) || write.Level is < 1 or > 5)
            problems.Add(new("question.invalid", "Language, category, prompt and a level from 1 to 5 are required."));
        if (write.Language is not null && !state.Languages.Contains(write.Language, StringComparer.OrdinalIgnoreCase))
            problems.Add(new("language.unsupported", "The question language is not enabled for this tenant."));
        if (write.CategoryId is not null && !state.Categories.Contains(write.CategoryId.Value))
            problems.Add(new("category.unknown", "The question category is not available in this tenant."));
        try
        {
            problems.AddRange(kind.Validate(write.Payload, new ValidateEnv(write.Language?.Trim() ?? ""))
                .Select(problem => new ContentProblem(problem.Code, problem.Message)));
        }
        catch (JsonException exception)
        {
            problems.Add(new("payload.malformed", exception.Message));
        }
        return problems;
    }

    private static Duplicate FindDuplicate(QuestionWrite write, IReadOnlyList<QuestionEnvelope> existing, IReadOnlyList<ImportCandidate> accepted)
    {
        var question = existing.FirstOrDefault(item => string.Equals(item.Language, write.Language, StringComparison.OrdinalIgnoreCase)
            && item.CategoryId == write.CategoryId && PromptFingerprint.AreDuplicates(item.Prompt, write.Prompt));
        if (question is not null) return new(question.Id, null);
        question = existing.FirstOrDefault(item => write.Topic is not null && item.Topic == write.Topic
            && string.Equals(item.Language, write.Language, StringComparison.OrdinalIgnoreCase) && item.Family == write.Family);
        if (question is not null) return new(question.Id, null);
        var earlier = accepted.FirstOrDefault(item => string.Equals(item.Write.Language, write.Language, StringComparison.OrdinalIgnoreCase)
            && item.Write.CategoryId == write.CategoryId && PromptFingerprint.AreDuplicates(item.Write.Prompt, write.Prompt));
        if (earlier is not null) return new(null, earlier.Row);
        earlier = accepted.FirstOrDefault(item => write.Topic is not null && item.Write.Topic == write.Topic
            && string.Equals(item.Write.Language, write.Language, StringComparison.OrdinalIgnoreCase) && item.Write.Family == write.Family);
        return earlier is null ? new(null, null) : new(null, earlier.Row);
    }

    private static ImportRowReport DuplicateAfterCommit(ImportCandidate candidate, IReadOnlyList<QuestionEnvelope> questions)
    {
        var duplicate = FindDuplicate(candidate.Write, questions, []);
        return new(candidate.Row, "duplicate", null, duplicate.QuestionId, duplicate.Row);
    }

    private static ImportExample Example(IImports mapping, bool csv) => mapping switch
    {
        IImports<ChoicePayload> typed => csv ? typed.CsvExample : typed.JsonExample,
        IImports<OrderPayload> typed => csv ? typed.CsvExample : typed.JsonExample,
        IImports<MapPickPayload> typed => csv ? typed.CsvExample : typed.JsonExample,
        IImports<PollChoicePayload> typed => csv ? typed.CsvExample : typed.JsonExample,
        IImports<PollRosterPayload> typed => csv ? typed.CsvExample : typed.JsonExample,
        _ => throw new InvalidOperationException("The selected kind does not provide import examples."),
    };

    private static JsonElement Payload(IKindRuntime kind, IImports mapping, ImportRow row) => kind switch
    {
        ChoiceKind choice => JsonSerializer.SerializeToElement(((IImports<ChoicePayload>)choice).Convert(row), ChoiceJsonContext.Default.ChoicePayload),
        OrderKind order => JsonSerializer.SerializeToElement(((IImports<OrderPayload>)order).Convert(row), OrderJsonContext.Default.OrderPayload),
        MapPickKind map => JsonSerializer.SerializeToElement(((IImports<MapPickPayload>)map).Convert(row), MapPickJsonContext.Default.MapPickPayload),
        PollChoiceKind pollChoice => JsonSerializer.SerializeToElement(((IImports<PollChoicePayload>)pollChoice).Convert(row), PollChoiceJsonContext.Default.PollChoicePayload),
        PollRosterKind pollRoster => JsonSerializer.SerializeToElement(((IImports<PollRosterPayload>)pollRoster).Convert(row), PollRosterJsonContext.Default.PollRosterPayload),
        _ => throw new InvalidOperationException($"Kind '{kind.Id}' does not provide a supported import payload."),
    };

    private static bool TryKind(string? requested, IKindRegistry kinds, out IKindRuntime? kind, out IImports? mapping)
    {
        var id = requested?.Trim().ToLowerInvariant() switch
        {
            "choice" => "choice",
            "order" or "sort" => "order",
            "map_pick" or "map" => "map_pick",
            "poll_choice" => "poll_choice",
            "poll_roster" => "poll_roster",
            _ => null,
        };
        if (id is null || !kinds.TryGetCurrent(id, out kind) || kind is not IImports imports)
        {
            kind = null;
            mapping = null;
            return false;
        }
        mapping = imports;
        return true;
    }

    private static string? Value(IFormCollection form, string name) => form.TryGetValue(name, out var value) ? value.ToString() : null;
    private static string? Field(ImportRow row, string primary, string legacy) => row.Field(primary) ?? row.Field(legacy);
    private static string? FileFormat(string? fileName) => Path.GetExtension(fileName ?? "").TrimStart('.').ToLowerInvariant() is var extension && extension is "csv" or "json" ? extension : null;
    private static string? LegacyKind(string id) => id switch { "choice" => "choice", "order" => "sort", "map_pick" => "map", _ => null };
    private static bool IsBundlePath(string value) => !Path.IsPathRooted(value) && !value.Contains('\\')
        && value.Split('/', StringSplitOptions.None).All(part => part.Length > 0 && part != "." && part != "..");
    private static bool TryMediaKind(string value, out MediaKind kind) => Enum.TryParse(value, true, out kind) && Enum.IsDefined(kind);
    private static bool TryBoolean(string? value, bool fallback, out bool result) => string.IsNullOrWhiteSpace(value) ? (result = fallback) == fallback : bool.TryParse(value, out result);
    private static bool TryStatus(string? value, QuestionStatus fallback, out QuestionStatus result) => string.IsNullOrWhiteSpace(value) ? (result = fallback) == fallback : Enum.TryParse(value, true, out result) && Enum.IsDefined(result);
    private static IResult Error(string error, int status) => Results.Json(new { error }, statusCode: status);
    private static ImportReport Report(IReadOnlyList<ImportRowReport> rows, IReadOnlyList<ImportWarning>? warnings = null) => new(rows.OrderBy(row => row.Row).ToArray(), rows.Count(row => row.Outcome is "imported" or "would_import"), rows.Count(row => row.Outcome is not ("imported" or "would_import")), warnings ?? []);
    private static byte[] Csv(IReadOnlyDictionary<string, string?> fields) => System.Text.Encoding.UTF8.GetBytes(string.Join(',', fields.Keys) + "\n" + string.Join(',', fields.Values.Select(value => '"' + (value ?? "").Replace("\"", "\"\"") + '"')) + "\n");

    private static IReadOnlyList<ImportWarning> Warnings(ImportState state, IReadOnlyList<ImportCandidate> accepted)
    {
        var warnings = new List<ImportWarning>();
        var acceptedWithLanguage = accepted.Where(candidate => candidate.Write.Language is not null).ToArray();
        var importedLevels = state.Questions.Select(question => (question.Language, question.CategoryId, question.Level))
            .Concat(acceptedWithLanguage.Select(candidate => (Language: candidate.Write.Language!, candidate.Write.CategoryId, candidate.Write.Level)));
        foreach (var (language, category) in acceptedWithLanguage.Select(candidate => (candidate.Write.Language!, candidate.Write.CategoryId)).Distinct())
        {
            if (category is null) continue;
            var levels = importedLevels.Where(item => string.Equals(item.Language, language, StringComparison.OrdinalIgnoreCase) && item.CategoryId == category)
                .Select(item => item.Level).ToHashSet();
            var missing = Enumerable.Range(1, 5).Where(level => !levels.Contains(level)).ToArray();
            if (missing.Length > 0)
                warnings.Add(new("levels.incomplete", $"{language}/{category} has no questions at levels {string.Join(", ", missing)}."));
        }

        var choices = accepted.Where(candidate => candidate.Write.Kind == "choice")
            .Select(candidate => JsonSerializer.Deserialize(candidate.Write.Payload, ChoiceJsonContext.Default.ChoicePayload))
            .Where(payload => payload is not null)
            .Cast<ChoicePayload>()
            .ToArray();
        if (choices.Length > 0 && (AnswerLengthRate(choices, true) > 0.40 || AnswerLengthRate(choices, false) > 0.40))
            warnings.Add(new("choice.answer_length_tell", "Correct choice options are disproportionately the longest or shortest answer."));
        return warnings;
    }

    private static double AnswerLengthRate(IReadOnlyList<ChoicePayload> choices, bool longest)
    {
        var wins = 0.0;
        foreach (var choice in choices)
        {
            var lengths = choice.Options!.Select(option => option.Length).ToArray();
            var target = longest ? lengths.Max() : lengths.Min();
            if (lengths[choice.CorrectIndex] == target) wins += 1.0 / lengths.Count(length => length == target);
        }
        return wins / choices.Count;
    }

    private sealed record ImportState(IReadOnlyList<QuestionEnvelope> Questions, IReadOnlyList<string> Languages, IReadOnlyList<Guid> Categories);
    private sealed record CandidateBuild(ContentProblem? Error = null, QuestionWrite? Write = null, MediaReference? Media = null)
    {
        public CandidateBuild(QuestionWrite write) : this(null, write) { }
        public CandidateBuild(ContentProblem error) : this(error, null) { }
        public CandidateBuild(QuestionWrite write, MediaReference media) : this(null, write, media) { }
    }
    private sealed record ImportCandidate(int Row, QuestionWrite Write, MediaReference? Media);
    private sealed record MediaReference(string? Value, string? Kind, string? Attribution);
    private sealed record MediaImportResult(string? Error = null, Guid? AssetId = null);
    private sealed record Duplicate(Guid? QuestionId, int? Row);
    private sealed record ImportRowReport(int Row, string Outcome, IReadOnlyList<ContentProblem>? Problems = null, Guid? ExistingQuestionId = null, int? ExistingRow = null, Guid? QuestionId = null);
    private sealed record ImportWarning(string Code, string Message);
    private sealed record ImportReport(IReadOnlyList<ImportRowReport> Rows, int Accepted, int Rejected, IReadOnlyList<ImportWarning> Warnings);
}
