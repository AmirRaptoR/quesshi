using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;
using Quesshi.Application;
using Quesshi.Plugin.Api;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quesshi.Server.Tenants;

internal static class TenantResourceEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly IReadOnlyDictionary<string, string> DefaultPalette = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["shab"] = "#090E22",
        ["lajevard"] = "#16225C",
        ["lajevard-up"] = "#1E2E78",
        ["firouzeh"] = "#2FBFB0",
        ["zaferan"] = "#E8A83A",
        ["anaar"] = "#D4425C",
        ["sadaf"] = "#EDE3D0",
        ["sadaf-2"] = "rgb(237 227 208 / 0.66)",
        ["sadaf-3"] = "rgb(237 227 208 / 0.38)",
        ["line"] = "rgb(47 191 176 / 0.18)",
        ["line-2"] = "rgb(47 191 176 / 0.34)"
    };

    private static readonly CapabilityType[] CapabilityTypes = Enum.GetValues<CapabilityType>();
    private static readonly string ServerBuildId = typeof(TenantResourceEndpoints).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(TenantResourceEndpoints).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    public static IEndpointRouteBuilder MapTenantResources(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/config", WriteConfigAsync);
        endpoints.MapGet("/theme.css", WriteThemeAsync);
        endpoints.MapGet("/manifest.webmanifest", WriteManifestAsync);
        return endpoints;
    }

    private static async Task WriteConfigAsync(HttpContext context, ITenantDb tenantDb, CapabilityRegistry registry, CancellationToken cancellationToken)
    {
        var data = await LoadAsync(context, tenantDb, cancellationToken);
        var appNames = Branding.ReadAppNames(data.Branding);
        var capabilities = CapabilityTypes.ToDictionary(
            type => type.ToString().ToLowerInvariant(),
            type => data.Capabilities
                .Where(capability => string.Equals(capability.Type, type.ToString(), StringComparison.OrdinalIgnoreCase)
                    && registry.TryGet(type, capability.Key) is not null)
                .Select(capability => capability.Key)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            StringComparer.Ordinal);
        var document = new ConfigDocument(
            data.Name,
            data.Languages.Select(language => new LanguageDocument(language.Code, language.Name, language.Direction, appNames.GetValueOrDefault(language.Code))).ToArray(),
            data.DefaultLanguage,
            capabilities,
            data.ConfigVersion,
            ServerBuildId);
        await WriteAsync(context, JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions), "application/json; charset=utf-8", cancellationToken);
    }

    private static async Task WriteThemeAsync(HttpContext context, ITenantDb tenantDb, CancellationToken cancellationToken)
    {
        var data = await LoadAsync(context, tenantDb, cancellationToken);
        var palette = Branding.ReadPalette(data.Branding);
        var css = new StringBuilder(320).AppendLine(":root {");
        foreach (var (name, fallback) in DefaultPalette)
        {
            css.Append("  --").Append(name).Append(": ").Append(palette.GetValueOrDefault(name, fallback)).AppendLine(";");
        }
        css.AppendLine("}");
        await WriteAsync(context, Encoding.UTF8.GetBytes(css.ToString()), "text/css; charset=utf-8", cancellationToken);
    }

    private static async Task WriteManifestAsync(HttpContext context, ITenantDb tenantDb, CancellationToken cancellationToken)
    {
        var data = await LoadAsync(context, tenantDb, cancellationToken);
        var palette = Branding.ReadPalette(data.Branding);
        var color = palette.GetValueOrDefault("shab", DefaultPalette["shab"]);
        var document = new ManifestDocument(data.Name, data.Name, color, color, Branding.ReadIcons(data.Branding));
        await WriteAsync(context, JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions), "application/manifest+json; charset=utf-8", cancellationToken);
    }

    private static async Task<TenantResourceData> LoadAsync(HttpContext context, ITenantDb tenantDb, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context) ?? throw new InvalidOperationException("A tenant resource requires tenant resolution.");
        return await tenantDb.InTenantAsync(tenant.Id, async transaction =>
        {
            var settings = await transaction.QuerySingleOrDefaultAsync<TenantSettings>("""
                select name as Name, default_language as DefaultLanguage, branding as Branding, config_version as ConfigVersion
                from tenants
                """, cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException("Resolved tenant no longer exists.");
            var languages = await transaction.QueryAsync<TenantLanguage>("""
                select code as Code, name as Name, direction as Direction
                from tenant_languages
                order by sort_order, code
                """, cancellationToken: cancellationToken);
            var capabilities = await transaction.QueryAsync<TenantCapability>("""
                select type as Type, key as Key
                from tenant_capabilities
                where entitled and enabled
                """, cancellationToken: cancellationToken);
            return new TenantResourceData(settings.Name, settings.DefaultLanguage, settings.Branding, settings.ConfigVersion, languages, capabilities);
        }, cancellationToken: cancellationToken);
    }

    private static async Task WriteAsync(HttpContext context, byte[] content, string contentType, CancellationToken cancellationToken)
    {
        var etag = $"\"{Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()}\"";
        context.Response.Headers.ETag = etag;
        context.Response.Headers.CacheControl = "no-cache";
        if (Matches(context.Request.Headers.IfNoneMatch, etag))
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        context.Response.ContentType = contentType;
        context.Response.ContentLength = content.Length;
        await context.Response.Body.WriteAsync(content, cancellationToken);
    }

    private static bool Matches(string? ifNoneMatch, string etag) =>
        !string.IsNullOrWhiteSpace(ifNoneMatch)
        && ifNoneMatch.Split(',').Select(value => value.Trim()).Any(value => value is "*" || string.Equals(value, etag, StringComparison.Ordinal));

    private sealed record TenantSettings(string Name, string DefaultLanguage, JsonElement Branding, long ConfigVersion);
    private sealed record TenantLanguage(string Code, string Name, string Direction);
    private sealed record TenantCapability(string Type, string Key);
    private sealed record TenantResourceData(string Name, string DefaultLanguage, JsonElement Branding, long ConfigVersion, IReadOnlyList<TenantLanguage> Languages, IReadOnlyList<TenantCapability> Capabilities);
    private sealed record ConfigDocument(string Name, IReadOnlyList<LanguageDocument> Languages, string DefaultLanguage, IReadOnlyDictionary<string, string[]> Capabilities, long ConfigVersion, string ServerBuildId);
    private sealed record LanguageDocument(string Code, string DisplayName, string Direction, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AppName);
    private sealed record ManifestDocument(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("short_name")] string ShortName,
        [property: JsonPropertyName("background_color")] string BackgroundColor,
        [property: JsonPropertyName("theme_color")] string ThemeColor,
        [property: JsonPropertyName("icons")] IReadOnlyList<ManifestIcon> Icons);
    private sealed record ManifestIcon(
        [property: JsonPropertyName("src")] string Src,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("sizes")] string Sizes,
        [property: JsonPropertyName("purpose")] string? Purpose = null);

    private static class Branding
    {
        private static readonly ManifestIcon[] DefaultIcons =
        [
            new("icon-192.png", "image/png", "192x192"),
            new("icon-512.png", "image/png", "512x512", "any maskable")
        ];

        public static IReadOnlyDictionary<string, string> ReadPalette(JsonElement branding)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var palette = GetObject(branding, "palette") ?? GetObject(branding, "theme") ?? GetObject(branding, "colors");
            foreach (var name in DefaultPalette.Keys)
            {
                if (TryGetString(palette, name, out var color) && IsHexColor(color)) result[name] = color;
            }
            return result;
        }

        public static IReadOnlyDictionary<string, string> ReadAppNames(JsonElement branding)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var names = GetObject(branding, "appNames");
            if (names is not { } appNames) return result;
            foreach (var property in appNames.EnumerateObject())
            {
                if (property.Value.ValueKind is JsonValueKind.String
                    && property.Name.Length is > 0 and <= 32
                    && property.Value.GetString() is { Length: > 0 and <= 160 } value)
                {
                    result[property.Name] = value;
                }
            }
            return result;
        }

        public static IReadOnlyList<ManifestIcon> ReadIcons(JsonElement branding)
        {
            if (TryGetString(branding, "icon", out var icon) && IsLocalPath(icon))
            {
                return [new ManifestIcon(icon, "image/png", "any")];
            }
            return DefaultIcons;
        }

        private static JsonElement? GetObject(JsonElement value, string propertyName) =>
            value.ValueKind is JsonValueKind.Object && value.TryGetProperty(propertyName, out var property) && property.ValueKind is JsonValueKind.Object
                ? property
                : null;

        private static bool TryGetString(JsonElement? value, string propertyName, out string result)
        {
            result = string.Empty;
            return value is { ValueKind: JsonValueKind.Object } objectValue
                && objectValue.TryGetProperty(propertyName, out var property)
                && property.ValueKind is JsonValueKind.String
                && (result = property.GetString() ?? string.Empty).Length > 0;
        }

        private static bool IsHexColor(string value) =>
            value.Length is 4 or 5 or 7 or 9
            && value[0] == '#'
            && value[1..].All(character => character is >= '0' and <= '9'
                or >= 'a' and <= 'f'
                or >= 'A' and <= 'F');

        private static bool IsLocalPath(string value) =>
            value.StartsWith("/", StringComparison.Ordinal)
            && !value.StartsWith("//", StringComparison.Ordinal)
            && !value.Contains('\\')
            && !value.Any(char.IsControl);
    }
}
