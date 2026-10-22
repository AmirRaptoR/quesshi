using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quesshi.Application;
using Quesshi.Application.Identity;
using Quesshi.Plugin.Api;
using Quesshi.Server.Api;
using Quesshi.Server.Sessions;
using Quesshi.Server.Tenants;

namespace Quesshi.Server.Security;

/// <summary>Lets a core endpoint module register routes without changing host startup.</summary>
public interface ICoreRouteModule
{
    void Map(CoreRouteBuilder routes);
}

/// <summary>Provides the only route groups used by core endpoint modules.</summary>
public sealed class CoreRouteBuilder
{
    internal CoreRouteBuilder(RouteGroupBuilder playerRoutes, RouteGroupBuilder openRoutes, RouteGroupBuilder adminRoutes)
    {
        PlayerRoutes = playerRoutes;
        OpenRoutes = openRoutes;
        _adminRoutes = adminRoutes;
    }

    public RouteGroupBuilder PlayerRoutes { get; }

    public RouteGroupBuilder OpenRoutes { get; }

    private readonly RouteGroupBuilder _adminRoutes;

    /// <summary>Creates a role-protected core administration route group below <c>/api/admin</c>.</summary>
    public RouteGroupBuilder AdminRoutes(AdminRole minimumRole) => RouteSecurity.SecureAdminRoutes(_adminRoutes.MapGroup(string.Empty), minimumRole);

    /// <summary>Creates a player-operations route group below <c>/api/admin</c>.</summary>
    public RouteGroupBuilder PlayerOperationRoutes() => RouteSecurity.SecurePlayerOperationRoutes(_adminRoutes.MapGroup(string.Empty));
}

internal sealed record AdminRouteSecurityMetadata(AdminRole MinimumRole, bool RequiresPlayerOperationCapability = false);

internal sealed class AdminRouteSecurityMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, AdminAuditLog audit, AdminAntiforgery antiforgery)
    {
        var policy = context.GetEndpoint()?.Metadata.GetMetadata<AdminRouteSecurityMetadata>();
        if (policy is null)
        {
            await next(context);
            return;
        }

        var admin = context.Admin();
        if (admin is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (RouteSecurity.IsUnsafeMethod(context.Request.Method))
        {
            await audit.WriteRequestAsync(context, context.RequestAborted);
        }

        if (admin.MustChangePassword
            || (policy.RequiresPlayerOperationCapability ? !admin.CanManagePlayers : !admin.HasRole(policy.MinimumRole)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (RouteSecurity.IsUnsafeMethod(context.Request.Method))
        {
            if (!RouteSecurity.IsSameTenantOrigin(context))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (!context.Request.Headers.TryGetValue("X-CSRF-Admin", out var token)
                || !antiforgery.IsValid(context, token.ToString()))
            {
                await Results.BadRequest(new { error = "invalid_antiforgery" }).ExecuteAsync(context);
                return;
            }
        }

        await next(context);
    }
}

internal static class RouteSecurity
{
    public static void MapRoutes(this WebApplication app)
    {
        MediaEndpoints.MapPublic(app);
        var api = app.MapGroup("/api");
        var playerRoutes = SecurePlayerRoutes(api.MapGroup(string.Empty));
        var openRoutes = SecureOpenRoutes(api.MapGroup(string.Empty));
        var openAdminAuthRoutes = SecureAdminAntiforgery(api.MapGroup("/admin/auth"));
        var adminAuthRoutes = SecureAuthenticatedAdminRoutes(api.MapGroup("/admin/auth"));
        var adminRoutes = api.MapGroup("/admin");

        api.MapGet("/antiforgery", (HttpContext context, PlayerAntiforgery antiforgery) =>
        {
            context.Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
            context.Response.Headers.Pragma = "no-cache";
            return Results.Ok(new { token = antiforgery.Issue(context) });
        });
        api.MapGet("/admin/antiforgery", (HttpContext context, AdminAntiforgery antiforgery) =>
        {
            context.Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
            context.Response.Headers.Pragma = "no-cache";
            return Results.Ok(new { token = antiforgery.Issue(context) });
        });

        openRoutes.MapPost("/auth/sign-out", SignOutAsync);
        playerRoutes.MapSessionCommands();
        AdminAuthEndpoints.Map(openAdminAuthRoutes, adminAuthRoutes);

        var routes = new CoreRouteBuilder(playerRoutes, openRoutes, adminRoutes);
        foreach (var module in DiscoverCoreModules()) module.Map(routes);

        var features = app.Services.GetRequiredService<FeatureRegistry>();
        foreach (var feature in features.Routes)
        {
            var featureApi = app.MapGroup($"/api/x/{feature.Id}");
            var featurePlayerRoutes = SecurePlayerRoutes(featureApi.MapGroup(string.Empty));
            var featureOpenRoutes = SecureOpenRoutes(featureApi.MapGroup(string.Empty));
            featurePlayerRoutes.AddEndpointFilter(RequireCapabilityAsync(feature.Id));
            featureOpenRoutes.AddEndpointFilter(RequireCapabilityAsync(feature.Id));

            foreach (var contribution in feature.Player) contribution(featurePlayerRoutes);
            foreach (var contribution in feature.Open) contribution(featureOpenRoutes);
            foreach (var admin in feature.Admin)
            {
                var featureAdminRoutes = SecureAdminRoutes(app.MapGroup($"/api/admin/x/{feature.Id}"), admin.MinimumRole);
                featureAdminRoutes.AddEndpointFilter(RequireCapabilityAsync(feature.Id));
                foreach (var contribution in admin.Contributions) contribution(featureAdminRoutes);
            }
        }

        foreach (var signIn in app.Services.GetService<SignInRegistry>()?.SignIns ?? [])
        {
            var signInRoutes = SecureOpenRoutes(app.MapGroup($"/api/auth/x/{signIn.Id}"));
            signInRoutes.AddEndpointFilter(RequireCapabilityAsync(CapabilityType.Signin, signIn.Id));
            signIn.SignIn.MapOpenRoutes(signInRoutes);
        }
    }

    private static RouteGroupBuilder SecurePlayerRoutes(RouteGroupBuilder group)
    {
        group.AddEndpointFilter(RequirePlayerAsync);
        group.AddEndpointFilter(RequireAntiforgeryAsync);
        return group;
    }

    private static RouteGroupBuilder SecureOpenRoutes(RouteGroupBuilder group)
    {
        group.AddEndpointFilter(RequireAntiforgeryAsync);
        return group;
    }

    private static RouteGroupBuilder SecureAuthenticatedAdminRoutes(RouteGroupBuilder group)
    {
        group.AddEndpointFilter(RequireAdminAsync);
        return SecureAdminAntiforgery(group);
    }

    internal static RouteGroupBuilder SecureAdminRoutes(RouteGroupBuilder group, AdminRole minimumRole)
    {
        if (!Enum.IsDefined(minimumRole)) throw new ArgumentOutOfRangeException(nameof(minimumRole));
        group.WithMetadata(new AdminRouteSecurityMetadata(minimumRole));
        return group;
    }

    internal static RouteGroupBuilder SecurePlayerOperationRoutes(RouteGroupBuilder group)
    {
        group.WithMetadata(new AdminRouteSecurityMetadata(AdminRole.Moderator, RequiresPlayerOperationCapability: true));
        return group;
    }

    private static RouteGroupBuilder SecureAdminAntiforgery(RouteGroupBuilder group)
    {
        group.AddEndpointFilter(RequireAdminAntiforgeryAsync);
        return group;
    }

    private static async ValueTask<object?> SignOutAsync(HttpContext context, IPlayerSessionResolver sessions)
    {
        if (context.Request.Cookies.TryGetValue(PlayerSessionService.CookieName, out var token))
        {
            await sessions.RevokeAsync(token, context.RequestAborted);
        }

        context.Response.Cookies.Delete(PlayerSessionService.CookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
        });
        return Results.NoContent();
    }

    private static async ValueTask<object?> RequirePlayerAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (context.HttpContext.IsBannedPlayer()) return Results.Json(new { error = "banned" }, statusCode: StatusCodes.Status403Forbidden);
        var player = context.HttpContext.Player();
        if (player is null) return Results.Unauthorized();
        if (player.IsGuest && context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<AllowGuestsMetadata>() is null)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        return await next(context);
    }

    private static async ValueTask<object?> RequireAdminAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (context.HttpContext.Admin() is null) return Results.Unauthorized();
        return await next(context);
    }

    private static async ValueTask<object?> RequireAntiforgeryAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        if (!IsUnsafeMethod(request.Method))
        {
            return await next(context);
        }

        if (!IsSameTenantOrigin(context.HttpContext)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!request.Headers.TryGetValue("X-CSRF", out var token)
            || !context.HttpContext.RequestServices.GetRequiredService<PlayerAntiforgery>().IsValid(context.HttpContext, token.ToString()))
        {
            return Results.BadRequest(new { error = "invalid_antiforgery" });
        }

        return await next(context);
    }

    private static async ValueTask<object?> RequireAdminAntiforgeryAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        if (!IsUnsafeMethod(request.Method)) return await next(context);
        if (!IsSameTenantOrigin(context.HttpContext)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!request.Headers.TryGetValue("X-CSRF-Admin", out var token)
            || !context.HttpContext.RequestServices.GetRequiredService<AdminAntiforgery>().IsValid(context.HttpContext, token.ToString()))
        {
            return Results.BadRequest(new { error = "invalid_antiforgery" });
        }
        return await next(context);
    }

    private static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> RequireCapabilityAsync(string featureId) =>
        RequireCapabilityAsync(CapabilityType.Feature, featureId);

    private static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> RequireCapabilityAsync(CapabilityType type, string key) =>
        async (context, next) =>
        {
            var tenant = TenantResolutionMiddleware.GetTenant(context.HttpContext);
            if (tenant is null) return Results.NotFound();

            var capabilities = context.HttpContext.RequestServices.GetRequiredService<ICapabilities>();
            if (!await capabilities.IsOnAsync(tenant.Id, type, key, context.HttpContext.RequestAborted))
            {
                return Results.NotFound();
            }

            return await next(context);
        };

    internal static bool IsUnsafeMethod(string method) => !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method);

    internal static bool IsSameTenantOrigin(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue("Origin", out var origin)
            || !Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
        {
            return false;
        }

        var host = context.Request.Host;
        return (string.Equals(originUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(originUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            && string.Equals(originUri.Scheme, context.Request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(originUri.Host, host.Host, StringComparison.OrdinalIgnoreCase)
            && originUri.Port == (host.Port ?? DefaultPort(originUri.Scheme));
    }

    private static int DefaultPort(string scheme) => string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80;

    private static IEnumerable<ICoreRouteModule> DiscoverCoreModules() =>
        typeof(Program).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(ICoreRouteModule).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .Select(type => Activator.CreateInstance(type) as ICoreRouteModule
                ?? throw new InvalidOperationException($"Core route module '{type.FullName}' requires a public parameterless constructor."));
}
