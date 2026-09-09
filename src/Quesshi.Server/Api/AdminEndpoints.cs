using Quesshi.Application.Ports;
using Quesshi.Infrastructure.Generation;
using Quesshi.Application.UseCases;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;
using Quesshi.Shared;

namespace Quesshi.Server.Api;

public static class AdminEndpoints
{
    private const long MaxUploadBytes = 20 * 1024 * 1024;

    private static readonly Dictionary<string, MediaKind> AllowedMedia = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = MediaKind.Image, [".jpg"] = MediaKind.Image, [".jpeg"] = MediaKind.Image,
        [".webp"] = MediaKind.Image, [".gif"] = MediaKind.Image,
        [".mp3"] = MediaKind.Audio, [".m4a"] = MediaKind.Audio, [".ogg"] = MediaKind.Audio,
        [".mp4"] = MediaKind.Video, [".webm"] = MediaKind.Video
    };

    public static void MapAdmin(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin").RequireAuthorization("admin");

        admin.MapGet("/dashboard", async (IPlayerRepository players, IQuestionRepository questions,
            IMatchArchive matches, IGenerationLog log, IQuestionGenerator generator, TopUpOptions topUp,
            IAiSpendLog spend, IClock clock, OpenRouterOptions ai, ILiveDirectory live, IGrainFactory grains) =>
        {
            var thin = ThinBuckets.Report(await questions.BucketCountsAsync(), topUp);

            return new AdminDashboardDto(
                await players.CountAsync(),
                await questions.CountAsync(new QuestionFilter()),
                await questions.CountAsync(new QuestionFilter(Status: QuestionStatus.Approved)),
                await questions.CountAsync(new QuestionFilter(Status: QuestionStatus.Pending)),
                await matches.CountAsync(),
                thin,
                [.. (await log.RecentAsync(10)).Select(r => r.ToDto())],
                generator.IsConfigured,
                (await spend.TotalsAsync()).ToDto(),
                (await spend.TotalsAsync(clock.Now.AddDays(-30))).ToDto(),
                ai.Model,
                await live.CountAsync(),
                await live.ConnectedCountAsync(),
                await live.QueueDepthAsync(),
                await matches.CountLiveAsync(),
                await grains.GetGrain<ILiveSettingsGrain>(0).IsEnabledAsync());
        });

        // --- live duels ----------------------------------------------------------------

        // A row whose duel could no longer be running — a hard process kill left it behind — is
        // dropped from the response and the index, so a wedged ghost cannot outlive its own duel.
        admin.MapGet("/live", async (ILiveDirectory live, IPlayerRepository players, IClock clock) =>
        {
            var rows = await live.AllAsync();
            var alive = new List<LiveDirectoryRow>();
            foreach (var row in rows)
            {
                var maxAge = LiveRules.LobbyExpires + (row.TotalRounds * (MatchRules.QuestionTime + LiveRules.RevealTime)) + TimeSpan.FromMinutes(1);
                if (clock.Now - row.StartedAt > maxAge) { await live.RemoveAsync(row.MatchId); continue; }
                alive.Add(row);
            }

            var ids = alive.SelectMany(r => r.Participants).Distinct().ToList();
            var names = (await players.GetManyAsync(ids)).ToDictionary(p => p.Id, p => p.DisplayName);

            // AdminLiveRowDto still only has room for two names — reshaping it for N seats is
            // Quesshi.Web's job (issue #53 rebuilds this table), not this endpoint's. Until then, every
            // seat after the owner's rides along comma-joined in the one field the DTO gives it, so a
            // four-player duel still names all four somewhere in the row instead of losing two of them
            // outright — "an admin sees four players" just does not yet mean "four columns".
            var ordered = alive.OrderBy(r => r.StartedAt).Take(200)
                .Select(r => new AdminLiveRowDto(r.MatchId, r.Code, names.GetValueOrDefault(r.Participants[0], r.Participants[0]),
                    string.Join(", ", r.Participants.Skip(1).Select(id => names.GetValueOrDefault(id, id))),
                    ((Language)r.Lang).Code(), r.RoundIndex, r.TotalRounds, ((LivePhase)r.Phase).ToString().ToLowerInvariant(), r.StartedAt))
                .ToList();

            return new AdminLivePageDto(ordered, alive.Count);
        });

        // No-contest only: no result, no stats, no leaderboard, for either player. Ending an
        // already-finished duel, or an id no live duel ever used, is a no-op that still answers 200 —
        // ILiveMatchGrain.EndAsync already does nothing once the duel is over or never existed.
        admin.MapPost("/live/{id}/end", async (string id, IGrainFactory grains) =>
        {
            await grains.GetGrain<ILiveMatchGrain>(id).EndAsync("ended by an administrator");
            return Results.Ok();
        });

        admin.MapPost("/live/enabled", async (bool value, IGrainFactory grains) =>
        {
            await grains.GetGrain<ILiveSettingsGrain>(0).SetEnabledAsync(value);
            return Results.Ok();
        });

        // --- questions ---------------------------------------------------------------
        admin.MapGet("/questions", async (string? lang, string? category, int? level, string? status, string? text,
            int? skip, int? take, IQuestionRepository questions) =>
        {
            var filter = new QuestionFilter(
                Lang: string.IsNullOrWhiteSpace(lang) ? null : lang.ToLanguage(),
                CategoryId: string.IsNullOrWhiteSpace(category) ? null : category,
                Level: level is null or 0 ? null : (Difficulty)level.Value,
                Status: ParseStatus(status),
                Text: string.IsNullOrWhiteSpace(text) ? null : text,
                Skip: skip ?? 0,
                Take: Math.Clamp(take ?? 25, 1, 100));

            return new AdminQuestionPageDto(
                [.. (await questions.FindAsync(filter)).Select(q => q.ToAdminDto())],
                await questions.CountAsync(filter));
        });

        // Writes a question of any kind, new or edited. Everything a kind may and may not carry is
        // decided in one place (QuestionSaveBinding) so that a rejection comes back as a code the
        // form can translate and show against the field that caused it, rather than as one English
        // sentence for all fourteen ways a question can be wrong.
        admin.MapPost("/questions", async (SaveQuestionDto body, IQuestionRepository questions, IClock clock, IIdFactory ids) =>
        {
            // The level arrives as a raw int; casting an out-of-range one would store a nonsense enum.
            if (!Enum.IsDefined((Difficulty)body.Level)) return Results.BadRequest(new { error = "bad_level" });

            if (QuestionSaveBinding.TryBind(body, out var kind, out var target, out var baseLayer) is { } error)
                return Results.BadRequest(new { error });

            var media = string.IsNullOrWhiteSpace(body.MediaUrl)
                ? MediaRef.None
                : new MediaRef(Enum.TryParse<MediaKind>(body.MediaKind, true, out var mediaKind) ? mediaKind : MediaKind.Image, body.MediaUrl!);

            var status = ParseStatus(body.Status) ?? QuestionStatus.Pending;
            var existing = string.IsNullOrWhiteSpace(body.Id) ? null : await questions.GetAsync(body.Id!);

            // WorldMapCountries.Codes is handed to the domain on both paths, not just on create: the
            // way a question ends up pointing at a country the map cannot draw is somebody editing an
            // old one, and validation that only ran for new questions would be validation that missed
            // the case it was written for.
            if (existing is not null)
            {
                existing.Edit(body.Lang.ToLanguage(), body.CategoryId, (Difficulty)body.Level,
                    body.Prompt, body.Choices, body.CorrectIndex, media, body.Explanation,
                    kind, target, baseLayer, WorldMapCountries.Codes);
                existing.SetStatus(status);

                await questions.UpsertAsync(existing);
                return Results.Ok(existing.ToAdminDto());
            }

            var created = Question.Create(ids.NewId(), body.Lang.ToLanguage(), body.CategoryId, (Difficulty)body.Level,
                body.Prompt, body.Choices, body.CorrectIndex, clock.Now, media, body.Explanation, QuestionSource.Admin, status,
                topic: null, kind: kind, target: target, baseLayer: baseLayer, knownCountryCodes: WorldMapCountries.Codes);

            await questions.UpsertAsync(created);
            return Results.Ok(created.ToAdminDto());
        });

        // The review queue: everything players have complained about, worst first.
        admin.MapGet("/reported", async (int? skip, int? take, IQuestionRepository questions, IPlayerRepository players) =>
        {
            var filter = new QuestionFilter(Reported: true, Skip: skip ?? 0, Take: Math.Clamp(take ?? 25, 1, 100));
            var found = await questions.FindAsync(filter);

            // Names, so the admin sees who complained rather than a row of opaque ids.
            var names = new Dictionary<string, string>();
            foreach (var id in found.SelectMany(q => q.Reports).Select(r => r.PlayerId).Distinct())
                names[id] = (await players.GetAsync(id))?.DisplayName ?? id;

            var items = found.Select(q => q.ToAdminDto() with
            {
                Reports = [.. q.Reports.Select(r => new QuestionReportDto(r.PlayerId,
                    names.GetValueOrDefault(r.PlayerId, r.PlayerId), r.Reason.ToString().ToLowerInvariant(), r.At))]
            }).ToList();

            return new AdminQuestionPageDto(items, await questions.CountAsync(filter));
        });

        // "I looked, it is fine": clears the complaints and puts it back in play.
        admin.MapPost("/questions/{id}/dismiss-reports", async (string id, IQuestionRepository questions) =>
        {
            if (await questions.GetAsync(id) is not { } question) return Results.NotFound();

            question.DismissReports();
            question.Approve();
            await questions.UpsertAsync(question);

            return Results.Ok(question.ToAdminDto());
        });

        admin.MapPost("/questions/{id}/approve", (string id, IQuestionRepository q) => SetStatusAsync(id, q, approve: true));
        admin.MapPost("/questions/{id}/reject", (string id, IQuestionRepository q) => SetStatusAsync(id, q, approve: false));

        admin.MapDelete("/questions/{id}", async (string id, IQuestionRepository questions) =>
        {
            await questions.DeleteAsync(id);
            return Results.Ok();
        });

        // --- categories --------------------------------------------------------------
        admin.MapGet("/categories", async (ICategoryRepository categories) =>
            (await categories.AllAsync()).Select(c => c.ToDto(Language.Fa)).ToList());

        // A new category is two names and a colour. The id, the position in the list and the icon are
        // all derivable — asking a human for them is three fields that can only be got wrong.
        admin.MapPost("/categories", async (CategoryDto body, ICategoryRepository categories) =>
        {
            var (iconFa, nameFa) = SplitIcon(body.NameFa);
            var (iconEn, nameEn) = SplitIcon(body.NameEn);
            var (iconNl, nameNl) = SplitIcon(body.NameNl);

            if (nameEn.Length == 0) return Results.BadRequest(new { error = "bad_name" });

            var slug = Slugify(body.Id.Length > 0 ? body.Id : nameEn);
            if (slug.Length == 0) return Results.BadRequest(new { error = "bad_id" });

            var all = await categories.AllAsync();
            var icon = new[] { iconFa, iconEn, iconNl, body.Icon.Trim() }.FirstOrDefault(i => i.Length > 0) ?? "";
            var order = body.SortOrder > 0
                ? body.SortOrder
                : (all.FirstOrDefault(c => c.Id == slug)?.SortOrder ?? all.Select(c => c.SortOrder).DefaultIfEmpty(0).Max() + 1);

            await categories.UpsertAsync(new Category(slug, nameFa, nameEn,
                icon.Length > 0 ? icon : "◆", body.Color, body.IsActive, order, nameNl));
            return Results.Ok();
        });

        admin.MapDelete("/categories/{id}", async (string id, ICategoryRepository categories, IQuestionRepository questions) =>
        {
            // Deleting a category with questions behind it would orphan them; deactivate instead.
            if (await questions.CountAsync(new QuestionFilter(CategoryId: id)) > 0)
                return Results.BadRequest(new { error = "category_in_use" });

            await categories.DeleteAsync(id);
            return Results.Ok();
        });

        // --- users -------------------------------------------------------------------
        admin.MapGet("/users", async (string? q, int? skip, int? take, IPlayerRepository players) =>
            new AdminUserPageDto(
                [.. (await players.SearchAsync(q, skip ?? 0, Math.Clamp(take ?? 25, 1, 100))).Select(p => p.ToAdminDto())],
                await players.CountAsync()));

        // Goes through IPlayerGrain, not IPlayerRepository — see IPlayerGrain.SetBannedAsync's own
        // remarks for why a direct repository write here is worse than PUT /api/me's own version of
        // the same bug: a later grain write (a settled match, a profile edit) could otherwise undo the
        // ban entirely, silently, with no admin action visible in between.
        admin.MapPost("/users/{id}/ban", async (string id, bool value, HttpContext ctx, IGrainFactory grains, IPlayerRepository players) =>
        {
            if (id == ctx.User.PlayerId()) return Results.BadRequest(new { error = "cannot_ban_self" });
            if (await players.GetAsync(id) is null) return Results.NotFound();

            return await grains.GetGrain<IPlayerGrain>(id).SetBannedAsync(value) ? Results.Ok() : Results.NotFound();
        });

        // --- generation --------------------------------------------------------------

        // Top up every thin bucket. Goes through the grain so two admins pressing it at the same
        // moment queue behind each other instead of both calling the model.
        admin.MapPost("/generate", async (IGrainFactory grains, IGenerationLog log) =>
        {
            var runId = await grains.GetGrain<IQuestionGeneratorGrain>(0).RunNowAsync();
            var run = (await log.RecentAsync(20)).FirstOrDefault(r => r.Id == runId);

            return run is null ? Results.Accepted() : Results.Ok(run.ToDto());
        });

        // Picture questions: same shape, but each one needs a freely-licensed photograph to exist.
        admin.MapPost("/generate/illustrated", async (GenerateRequestDto body, TopUpQuestionBank topUp) =>
        {
            if (!Enum.IsDefined((Difficulty)body.Level)) return Results.BadRequest(new { error = "bad_level" });

            var run = await topUp.GenerateIllustratedAsync(body.Lang.ToLanguage(), body.CategoryId,
                (Difficulty)body.Level, Math.Clamp(body.Count, 1, 20));

            return Results.Ok(run.ToDto());
        });

        // Generate into one bucket, on demand — one kind at a time, because each kind is a different
        // prompt and a different set of ways a candidate can be wrong, and an admin asking for sorts
        // wants to look at sorts.
        admin.MapPost("/generate/bucket", async (GenerateRequestDto body, TopUpQuestionBank topUp) =>
        {
            if (!Enum.IsDefined((Difficulty)body.Level)) return Results.BadRequest(new { error = "bad_level" });
            if (!QuestionSaveBinding.TryParseKind(body.Kind, out var kind)) return Results.BadRequest(new { error = "bad_kind" });

            var run = await topUp.GenerateOnceAsync(body.Lang.ToLanguage(), body.CategoryId,
                (Difficulty)body.Level, Math.Clamp(body.Count, 1, 20), kind);

            return Results.Ok(run.ToDto());
        });

        admin.MapGet("/generation", async (IGenerationLog log) =>
            (await log.RecentAsync(25)).Select(r => r.ToDto()).ToList());

        // --- media -------------------------------------------------------------------
        admin.MapPost("/media", async (IFormFile file, IWebHostEnvironment env, IIdFactory ids) =>
        {
            if (file.Length == 0 || file.Length > MaxUploadBytes) return Results.BadRequest(new { error = "bad_size" });

            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!AllowedMedia.TryGetValue(extension, out var kind)) return Results.BadRequest(new { error = "bad_type" });

            // Never trust the uploaded name: build our own so nothing can escape the media folder.
            var folder = Path.Combine(env.WebRootPath ?? "wwwroot", "media", "uploads");
            Directory.CreateDirectory(folder);

            var name = ids.NewId() + extension;
            await using (var stream = File.Create(Path.Combine(folder, name)))
                await file.CopyToAsync(stream);

            return Results.Ok(new MediaDto(kind.ToString().ToLowerInvariant(), $"/media/uploads/{name}"));
        }).DisableAntiforgery();
    }

    private static async Task<IResult> SetStatusAsync(string id, IQuestionRepository questions, bool approve)
    {
        var question = await questions.GetAsync(id);
        if (question is null) return Results.NotFound();

        if (approve) question.Approve(); else question.Reject();
        await questions.UpsertAsync(question);

        return Results.Ok(question.ToAdminDto());
    }

    private static QuestionStatus? ParseStatus(string? value)
        => Enum.TryParse<QuestionStatus>(value, true, out var parsed) ? parsed : null;

    private static string Slugify(string value)
    {
        var spaced = new string([.. value.Trim().ToLowerInvariant().Select(c => char.IsWhiteSpace(c) ? '-' : c)]);
        var kept = new string([.. spaced.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')]);

        // "Food & Cooking" would otherwise slug to "food--cooking".
        while (kept.Contains("--")) kept = kept.Replace("--", "-");
        return kept.Trim('-');
    }

    /// <summary>
    /// Splits a leading emoji off a category name, so "🍳 Cooking" is an icon and a name. Anything
    /// that starts with a letter, digit or ordinary punctuation is left alone.
    /// </summary>
    private static (string Icon, string Name) SplitIcon(string value)
    {
        var name = value.Trim();
        if (name.Length == 0) return ("", "");

        // Index-based overloads read the whole code point, so an emoji surrogate pair is one character.
        if (char.IsLetterOrDigit(name, 0) || char.IsPunctuation(name, 0)) return ("", name);

        var length = System.Globalization.StringInfo.GetNextTextElementLength(name);
        return (name[..length], name[length..].Trim());
    }
}
