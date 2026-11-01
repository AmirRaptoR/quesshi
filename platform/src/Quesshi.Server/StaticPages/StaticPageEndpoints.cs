using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;
using Quesshi.Application;
using Quesshi.Plugin.Api;
using Quesshi.Server.Security;
using Quesshi.Server.Tenants;

namespace Quesshi.Server.StaticPages;

/// <summary>
/// The server-rendered front pages: the signed-out landing page at <c>/</c> and the invite page at
/// <c>/join/{code}</c>. Both are static HTML per tenant and language and never look a lobby up.
/// </summary>
internal static partial class StaticPageEndpoints
{
    private const string LanguageCookie = "lang";
    private const string DefaultImage = "/icon-512.png";

    public static IEndpointRouteBuilder MapStaticPages(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/", LandingAsync);
        endpoints.MapGet("/join/{code}", JoinAsync);
        return endpoints;
    }

    private static async Task<IResult> LandingAsync(
        HttpContext context, ITenantDb tenantDb, StaticPageRenderer renderer, CancellationToken cancellationToken)
    {
        // A signed-in visitor, guest or banned player gets the app, which routes them onward.
        if (context.Player() is not null || context.IsBannedPlayer()) return AppShell();
        return await RenderAsync<LandingPage>(context, tenantDb, renderer, null, cancellationToken);
    }

    private static async Task<IResult> JoinAsync(
        string code, HttpContext context, ITenantDb tenantDb, StaticPageRenderer renderer, CancellationToken cancellationToken)
    {
        if (!InviteCode().IsMatch(code)) return AppShell();
        return await RenderAsync<JoinPage>(context, tenantDb, renderer, code, cancellationToken);
    }

    private static async Task<IResult> RenderAsync<TPage>(
        HttpContext context, ITenantDb tenantDb, StaticPageRenderer renderer, string? code, CancellationToken cancellationToken)
        where TPage : Microsoft.AspNetCore.Components.IComponent
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context) ?? throw new InvalidOperationException("A static page requires tenant resolution.");
        var data = await tenantDb.InTenantAsync(tenant.Id, transaction => ReadAsync(transaction, cancellationToken), readOnly: true, cancellationToken: cancellationToken);
        if (data is null) return Results.NotFound();

        var language = LanguageNegotiator.Choose(
            data.Languages,
            data.DefaultLanguage,
            context.Request.Cookies.TryGetValue(LanguageCookie, out var cookie) ? cookie : null,
            context.Request.Headers.AcceptLanguage.ToString());
        var origin = $"{context.Request.Scheme}://{context.Request.Host}";
        var model = new StaticPageModel(
            language.Code,
            language.Direction,
            data.AppNames.GetValueOrDefault(language.Code) ?? data.Name,
            data.LandingTexts.GetValueOrDefault(language.Code),
            origin,
            origin + data.Image,
            StaticTexts.For(language.Code));

        var html = typeof(TPage) == typeof(JoinPage)
            ? await renderer.RenderAsync<JoinPage>(model, new Dictionary<string, object?> { ["Code"] = code })
            : await renderer.RenderAsync<TPage>(model);

        context.Response.Headers.Vary = new[] { HeaderNames.Cookie, HeaderNames.AcceptLanguage };
        context.Response.Headers.CacheControl = "no-cache";
        return Results.Content(html, "text/html; charset=utf-8");
    }

    private static async Task<PageData?> ReadAsync(ITenantTx transaction, CancellationToken cancellationToken)
    {
        var settings = await transaction.QuerySingleOrDefaultAsync<TenantRow>(
            "select name as Name, default_language as DefaultLanguage, branding as Branding from tenants",
            cancellationToken: cancellationToken);
        if (settings is null) return null;
        var languages = (await transaction.QueryAsync<OfferedLanguage>(
            "select code as Code, direction as Direction from tenant_languages order by sort_order, code",
            cancellationToken: cancellationToken)).ToArray();
        return new PageData(
            settings.Name,
            settings.DefaultLanguage,
            languages,
            ReadTexts(settings.Branding, "appNames"),
            ReadTexts(settings.Branding, "landingTexts"),
            ReadImage(settings.Branding));
    }

    private static Dictionary<string, string> ReadTexts(JsonElement branding, string property)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (branding.ValueKind is JsonValueKind.Object
            && branding.TryGetProperty(property, out var texts)
            && texts.ValueKind is JsonValueKind.Object)
        {
            foreach (var entry in texts.EnumerateObject())
            {
                if (entry.Value.ValueKind is JsonValueKind.String && entry.Value.GetString() is { Length: > 0 } value) result[entry.Name] = value;
            }
        }
        return result;
    }

    /// <summary>The tenant's own icon when it has one, else the platform icon; only a same-origin path is trusted.</summary>
    private static string ReadImage(JsonElement branding)
    {
        if (branding.ValueKind is JsonValueKind.Object
            && branding.TryGetProperty("icon", out var icon)
            && icon.ValueKind is JsonValueKind.String
            && icon.GetString() is { } path
            && path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal)
            && !path.Contains('\\') && !path.Any(char.IsControl))
        {
            return path;
        }
        return DefaultImage;
    }

    private static IResult AppShell()
    {
        var index = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
        return File.Exists(index) ? Results.File(index, "text/html") : Results.NotFound();
    }

    [GeneratedRegex("^[A-Za-z0-9]{1,32}$")]
    private static partial Regex InviteCode();

    private sealed record TenantRow(string Name, string DefaultLanguage, JsonElement Branding);
    private sealed record PageData(
        string Name,
        string DefaultLanguage,
        IReadOnlyList<OfferedLanguage> Languages,
        Dictionary<string, string> AppNames,
        Dictionary<string, string> LandingTexts,
        string Image);
}
