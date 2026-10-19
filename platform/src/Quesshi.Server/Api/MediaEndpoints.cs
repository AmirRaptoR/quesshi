using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Quesshi.Application.Media;
using Quesshi.Plugin.Api;
using Quesshi.Server.Security;
using Quesshi.Server.Tenants;

namespace Quesshi.Server.Api;

/// <summary>Tenant-isolated delivery and editor ingestion of immutable media assets.</summary>
public sealed class MediaEndpoints : ICoreRouteModule
{
    public void Map(CoreRouteBuilder routes)
    {
        routes.AdminRoutes(AdminRole.Editor)
            .MapPost("/media", UploadAsync)
            .WithMetadata(new AdminAuditMetadata("admin.media.upload", "media"));
    }

    public static void MapPublic(WebApplication app) => app.MapGet("/media/{hash}", GetAsync);

    private static async Task<IResult> GetAsync(
        HttpContext context,
        string hash,
        [FromServices] IMediaStore media,
        CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        var asset = await media.FindByHashAsync(tenant.Id, hash, cancellationToken);
        if (asset is null) return Results.NotFound();
        var stream = await media.OpenReadAsync(tenant.Id, hash, cancellationToken);
        if (stream is null) return Results.NotFound();
        context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        return Results.File(stream, asset.Mime, enableRangeProcessing: asset.Kind is MediaKind.Audio or MediaKind.Video);
    }

    private static async Task<IResult> UploadAsync(
        HttpContext context,
        [FromServices] IMediaStore media,
        CancellationToken cancellationToken)
    {
        if (!context.Request.HasFormContentType) return Error("invalid_media", StatusCodes.Status400BadRequest);
        var form = await context.Request.ReadFormAsync(cancellationToken);
        if (form.Files.Count != 1) return Error("invalid_media", StatusCodes.Status400BadRequest);

        var file = form.Files[0];
        var declaredMime = NormalizeMime(file.ContentType);
        if (!TryKindFor(declaredMime, out var expectedKind)) return Error("unsupported_media", StatusCodes.Status400BadRequest);
        if (file.Length > Limit(expectedKind)) return Error("media_too_large", StatusCodes.Status400BadRequest);

        var bytes = await ReadLimitedAsync(file, Limit(expectedKind), cancellationToken);
        if (bytes is null) return Error("media_too_large", StatusCodes.Status400BadRequest);
        if (!MediaSniffer.TryInspect(bytes, out var actualKind, out var actualMime, out var meta)
            || actualKind != expectedKind
            || !string.Equals(actualMime, declaredMime, StringComparison.Ordinal))
        {
            return Error("media_type_mismatch", StatusCodes.Status400BadRequest);
        }

        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        var asset = await media.StoreAsync(tenant.Id, new MediaInput(actualKind, actualMime, bytes, meta, form["attribution"].ToString()), cancellationToken);
        return Results.Created($"/media/{asset.StorageKey}", new MediaResponse(asset));
    }

    private static async Task<byte[]?> ReadLimitedAsync(IFormFile file, long limit, CancellationToken cancellationToken)
    {
        await using var input = file.OpenReadStream();
        await using var output = new MemoryStream();
        var buffer = new byte[65_536];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > limit) return null;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return output.ToArray();
    }

    private static string NormalizeMime(string? value) => (value ?? string.Empty).Split(';', 2)[0].Trim().ToLowerInvariant();
    private static long Limit(MediaKind kind) => kind switch
    {
        MediaKind.Image => 5 * 1024 * 1024,
        MediaKind.Audio => 10 * 1024 * 1024,
        MediaKind.Video => 25 * 1024 * 1024,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static bool TryKindFor(string mime, out MediaKind kind)
    {
        kind = mime switch
        {
            "image/jpeg" or "image/png" or "image/webp" => MediaKind.Image,
            "audio/mpeg" or "audio/ogg" or "audio/wav" => MediaKind.Audio,
            "video/mp4" or "video/webm" => MediaKind.Video,
            _ => default,
        };
        return mime is "image/jpeg" or "image/png" or "image/webp"
            or "audio/mpeg" or "audio/ogg" or "audio/wav"
            or "video/mp4" or "video/webm";
    }

    private static IResult Error(string error, int statusCode) => Results.Json(new { error }, statusCode: statusCode);

    private sealed record MediaResponse(MediaAsset Asset)
    {
        public Guid Id { get; } = Asset.Id;
        public string Hash { get; } = Asset.StorageKey;
        public string Kind { get; } = Asset.Kind.ToString().ToLowerInvariant();
        public string Mime { get; } = Asset.Mime;
        public long Bytes { get; } = Asset.Bytes;
        public object Meta { get; } = Asset.Meta;
        public string? Attribution { get; } = Asset.Attribution;
    }
}
