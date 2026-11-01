using Microsoft.AspNetCore.Mvc;
using Quesshi.Plugin.Api;
using Quesshi.Server.Security;
using Quesshi.Server.Tenants;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Quesshi.Server.Api;

/// <summary>Owner-only editing of the tenant branding served as /theme.css, /manifest.webmanifest and /api/config.</summary>
public sealed class BrandingEndpoints : ICoreRouteModule
{
    private const int MaximumAppNameLength = 160;
    private const int MaximumLandingTextLength = 2000;

    public void Map(CoreRouteBuilder routes)
    {
        var branding = routes.AdminRoutes(AdminRole.Owner).MapGroup("/branding");
        branding.MapGet("", GetAsync);
        branding.MapPut("", UpdateAsync).WithMetadata(new AdminAuditMetadata("admin.branding.update", "branding"));
    }

    private static async Task<IResult> GetAsync(
        HttpContext context,
        [FromServices] ITenantDb database,
        CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();

        var document = await database.InTenantAsync(tenant.Id, async transaction =>
        {
            var row = await transaction.QuerySingleOrDefaultAsync<BrandingRow>(
                "select branding as Branding from tenants",
                cancellationToken: cancellationToken);
            return row is null ? null : ToResponse(row.Branding);
        }, cancellationToken: cancellationToken, readOnly: true);
        return document is null ? Results.NotFound() : Results.Ok(document);
    }

    private static async Task<IResult> UpdateAsync(
        HttpContext context,
        BrandingRequest? request,
        [FromServices] ITenantDb database,
        CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        if (request is null || !TryNormalize(request, out var write)) return Error("invalid_branding");

        var result = await database.InTenantAsync(tenant.Id, async transaction =>
        {
            var current = await transaction.QuerySingleOrDefaultAsync<BrandingRow>(
                "select branding as Branding from tenants for update",
                cancellationToken: cancellationToken);
            if (current is null) return BrandingChange.Missing;

            var offered = (await transaction.QueryAsync<string>(
                "select code from tenant_languages",
                cancellationToken: cancellationToken)).ToHashSet(StringComparer.Ordinal);
            if (write.AppNames.Keys.Concat(write.LandingTexts.Keys).Any(code => !offered.Contains(code)))
                return BrandingChange.InvalidLanguage;

            var logo = await FindImageAsync(transaction, write.LogoMediaId, cancellationToken);
            var icon = await FindImageAsync(transaction, write.IconMediaId, cancellationToken);
            if (write.LogoMediaId is not null && logo is null || write.IconMediaId is not null && icon is null)
                return BrandingChange.InvalidMedia;

            var branding = Merge(current.Branding, write, logo, icon);
            var version = await transaction.QuerySingleOrDefaultAsync<long?>("""
                update tenants
                set branding = cast(@branding as jsonb), config_version = config_version + 1
                where id = @tenantId
                returning config_version
                """, new { tenantId = tenant.Id, branding = branding.ToJsonString() }, cancellationToken);
            if (version is null) return BrandingChange.Missing;
            await transaction.ExecuteAsync(
                "select pg_notify('quesshi_config', @message)",
                new { message = $"config:{tenant.Id}:{version.Value}" },
                cancellationToken);
            return new BrandingChange(BrandingChangeStatus.Success, ToResponse(JsonSerializer.SerializeToElement(branding)));
        }, cancellationToken: cancellationToken);

        return result.Status switch
        {
            BrandingChangeStatus.Success => Results.Ok(result.Branding),
            BrandingChangeStatus.InvalidLanguage => Error("invalid_branding_language"),
            BrandingChangeStatus.InvalidMedia => Error("invalid_branding_media"),
            _ => Results.NotFound(),
        };
    }

    private static async Task<MediaRow?> FindImageAsync(ITenantTx transaction, Guid? id, CancellationToken cancellationToken) =>
        id is null
            ? null
            : await transaction.QuerySingleOrDefaultAsync<MediaRow>("""
                select id as Id, storage_key as StorageKey, mime as Mime
                from media_assets
                where id = @id and kind = 'image'
                """, new { id }, cancellationToken);

    private static JsonObject Merge(JsonElement current, BrandingWrite write, MediaRow? logo, MediaRow? icon)
    {
        var branding = current.ValueKind is JsonValueKind.Object
            ? JsonNode.Parse(current.GetRawText())!.AsObject()
            : new JsonObject();
        branding["palette"] = ToObject(write.Palette);
        branding["appNames"] = ToObject(write.AppNames);
        branding["landingTexts"] = ToObject(write.LandingTexts);
        SetMedia(branding, "logo", logo, withMime: false);
        SetMedia(branding, "icon", icon, withMime: true);
        return branding;
    }

    private static void SetMedia(JsonObject branding, string name, MediaRow? media, bool withMime)
    {
        branding.Remove(name);
        branding.Remove(name + "MediaId");
        branding.Remove(name + "Mime");
        if (media is null) return;
        branding[name] = $"/media/{media.StorageKey}";
        branding[name + "MediaId"] = media.Id;
        if (withMime) branding[name + "Mime"] = media.Mime;
    }

    private static JsonObject ToObject(IReadOnlyDictionary<string, string> values)
    {
        var result = new JsonObject();
        foreach (var (key, value) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal)) result[key] = value;
        return result;
    }

    private static BrandingResponse ToResponse(JsonElement branding) => new(
        ReadStrings(branding, "palette"),
        ReadString(branding, "logoMediaId") is { } logoId && Guid.TryParse(logoId, out var logo) ? logo : null,
        ReadString(branding, "logo"),
        ReadString(branding, "iconMediaId") is { } iconId && Guid.TryParse(iconId, out var icon) ? icon : null,
        ReadString(branding, "icon"),
        ReadStrings(branding, "appNames"),
        ReadStrings(branding, "landingTexts"));

    private static string? ReadString(JsonElement branding, string name) =>
        branding.ValueKind is JsonValueKind.Object
        && branding.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    private static SortedDictionary<string, string> ReadStrings(JsonElement branding, string name)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (branding.ValueKind is JsonValueKind.Object
            && branding.TryGetProperty(name, out var entries)
            && entries.ValueKind is JsonValueKind.Object)
        {
            foreach (var entry in entries.EnumerateObject())
            {
                if (entry.Value.ValueKind is JsonValueKind.String) result[entry.Name] = entry.Value.GetString()!;
            }
        }
        return result;
    }

    private static bool TryNormalize(BrandingRequest request, out BrandingWrite write)
    {
        write = null!;
        var tokens = TenantResourceEndpoints.PaletteTokens.ToHashSet(StringComparer.Ordinal);
        if (!TryClean(request.Palette, name => tokens.Contains(name), TenantResourceEndpoints.IsHexColor, 9, out var palette)
            || !TryClean(request.AppNames, _ => true, text => text.Length <= MaximumAppNameLength, MaximumAppNameLength, out var appNames)
            || !TryClean(request.LandingTexts, _ => true, text => text.Length <= MaximumLandingTextLength, MaximumLandingTextLength, out var landing))
        {
            return false;
        }

        write = new BrandingWrite(palette, request.LogoMediaId, request.IconMediaId, appNames, landing);
        return true;
    }

    /// <summary>Trims values, drops blank ones (clearing the entry) and rejects unknown keys or invalid values.</summary>
    private static bool TryClean(
        IReadOnlyDictionary<string, string?>? values,
        Func<string, bool> isKnownKey,
        Func<string, bool> isValid,
        int maximumLength,
        out Dictionary<string, string> cleaned)
    {
        cleaned = new Dictionary<string, string>(StringComparer.Ordinal);
        if (values is null) return true;
        foreach (var (key, raw) in values)
        {
            var name = key.Trim();
            if (name.Length is 0 or > 32 || !isKnownKey(name)) return false;
            var value = raw?.Trim();
            if (string.IsNullOrEmpty(value)) continue;
            if (value.Length > maximumLength || !isValid(value)) return false;
            cleaned[name] = value;
        }
        return true;
    }

    private static IResult Error(string error) => Results.Json(new { error }, statusCode: StatusCodes.Status400BadRequest);

    private sealed record BrandingRequest(
        Dictionary<string, string?>? Palette,
        Guid? LogoMediaId,
        Guid? IconMediaId,
        Dictionary<string, string?>? AppNames,
        Dictionary<string, string?>? LandingTexts);

    private sealed record BrandingWrite(
        Dictionary<string, string> Palette,
        Guid? LogoMediaId,
        Guid? IconMediaId,
        Dictionary<string, string> AppNames,
        Dictionary<string, string> LandingTexts);

    private sealed record BrandingRow(JsonElement Branding);

    private sealed record MediaRow(Guid Id, string StorageKey, string Mime);

    private sealed record BrandingResponse(
        SortedDictionary<string, string> Palette,
        Guid? LogoMediaId,
        string? LogoUrl,
        Guid? IconMediaId,
        string? IconUrl,
        SortedDictionary<string, string> AppNames,
        SortedDictionary<string, string> LandingTexts);

    private sealed record BrandingChange(BrandingChangeStatus Status, BrandingResponse? Branding = null)
    {
        public static BrandingChange Missing { get; } = new(BrandingChangeStatus.Missing);

        public static BrandingChange InvalidLanguage { get; } = new(BrandingChangeStatus.InvalidLanguage);

        public static BrandingChange InvalidMedia { get; } = new(BrandingChangeStatus.InvalidMedia);
    }

    private enum BrandingChangeStatus
    {
        Success,
        Missing,
        InvalidLanguage,
        InvalidMedia,
    }
}
