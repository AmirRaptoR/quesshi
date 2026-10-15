using Quesshi.Application.Identity;
using Quesshi.Infrastructure.Identity;
using Quesshi.Server.Security;
using Quesshi.Server.Tenants;

namespace Quesshi.Server.Api;

internal static class AdminAuthEndpoints
{
    public static void Map(RouteGroupBuilder openRoutes, RouteGroupBuilder authenticatedRoutes)
    {
        openRoutes.MapPost("/login", LoginAsync);
        openRoutes.MapPost("/forgot", ForgotAsync);
        openRoutes.MapPost("/reset", ResetAsync);
        authenticatedRoutes.MapPost("/password", ChangePasswordAsync);
        authenticatedRoutes.MapPost("/sign-out", SignOutAsync);
    }

    private static async Task<IResult> LoginAsync(HttpContext context, AdminLoginRequest body, AdminAuthService authentication, IAdminSessionIssuer sessions)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var result = await authentication.LoginAsync(tenant.Id, body.Username ?? string.Empty, body.Password ?? string.Empty, address, context.RequestAborted);
        if (result.Status == AdminLoginStatus.Locked)
            return Results.Json(new { code = "admin.auth.locked", lockedUntil = result.LockedUntil }, statusCode: StatusCodes.Status429TooManyRequests);
        if (result.Status != AdminLoginStatus.Success || result.AdminId is not { } adminId)
            return Results.Json(new { code = "admin.auth.invalid" }, statusCode: StatusCodes.Status401Unauthorized);
        if (!await sessions.IssueAsync(context, tenant.Id, adminId, result.MustChangePassword, context.RequestAborted))
            return Results.Json(new { code = "admin.auth.invalid" }, statusCode: StatusCodes.Status401Unauthorized);
        return Results.Ok(new { mustChangePassword = result.MustChangePassword });
    }

    private static async Task<IResult> ForgotAsync(HttpContext context, AdminForgotRequest body, AdminAuthService authentication)
    {
        var resetPage = $"{context.Request.Scheme}://{context.Request.Host}/admin/reset";
        await authentication.RequestResetAsync(body.UsernameOrEmail ?? string.Empty, resetPage, context.RequestAborted);
        return Results.Ok(new { sent = true });
    }

    private static async Task<IResult> ResetAsync(HttpContext context, AdminResetRequest body, AdminAuthService authentication) =>
        Answer(await authentication.ResetAsync(body.Token ?? string.Empty, body.NewPassword ?? string.Empty, context.RequestAborted));

    private static async Task<IResult> ChangePasswordAsync(HttpContext context, AdminChangePasswordRequest body, AdminAuthService authentication)
    {
        var admin = context.Admin();
        if (admin is null) return Results.Unauthorized();
        return Answer(await authentication.ChangePasswordAsync(admin.AdminId, body.CurrentPassword ?? string.Empty, body.NewPassword ?? string.Empty, context.RequestAborted));
    }

    private static async Task<IResult> SignOutAsync(HttpContext context, IAdminSessionResolver sessions)
    {
        if (context.Request.Cookies.TryGetValue(AdminSessionService.CookieName, out var token)) await sessions.RevokeAsync(token, context.RequestAborted);
        context.Response.Cookies.Delete(AdminSessionService.CookieName, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = "/" });
        return Results.NoContent();
    }

    private static IResult Answer(AdminPasswordChangeResult result) => result.Status switch
    {
        AdminPasswordChangeStatus.Success => Results.Ok(new { mustChangePassword = false }),
        AdminPasswordChangeStatus.WeakPassword => Results.Json(new { code = "admin.auth.weak", problems = result.Problems }, statusCode: StatusCodes.Status400BadRequest),
        AdminPasswordChangeStatus.WrongCurrentPassword => Results.Json(new { code = "admin.auth.wrongCurrent" }, statusCode: StatusCodes.Status400BadRequest),
        AdminPasswordChangeStatus.ExpiredToken => Results.Json(new { code = "admin.auth.linkExpired" }, statusCode: StatusCodes.Status400BadRequest),
        AdminPasswordChangeStatus.UsedToken => Results.Json(new { code = "admin.auth.linkUsed" }, statusCode: StatusCodes.Status400BadRequest),
        _ => Results.Json(new { code = "admin.auth.linkInvalid" }, statusCode: StatusCodes.Status400BadRequest)
    };

    private sealed record AdminLoginRequest(string? Username, string? Password);
    private sealed record AdminForgotRequest(string? UsernameOrEmail);
    private sealed record AdminResetRequest(string? Token, string? NewPassword);
    private sealed record AdminChangePasswordRequest(string? CurrentPassword, string? NewPassword);
}
