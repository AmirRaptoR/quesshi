using System.Security.Claims;
using Quesshi.Application.Ports;
using Quesshi.Application.UseCases;
using Quesshi.Domain;
using Quesshi.Server.Auth;
using Quesshi.Shared;

namespace Quesshi.Server.Api;

/// <summary>
/// Administrator sign-in: username, password, and a reset link by email. Separate from the game's
/// passwordless sign-in, and separate from its token.
/// </summary>
public static class AdminAuthEndpoints
{
    public static void MapAdminAuth(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/auth");

        group.MapPost("/login", async (AdminLoginDto body, AdminAuthService service, AdminTokenIssuer tokens) =>
        {
            var result = await service.LoginAsync(body.Username ?? "", body.Password ?? "");

            return result.Status switch
            {
                AdminLoginStatus.Ok or AdminLoginStatus.MustChangePassword =>
                    Results.Ok(new AdminSessionDto(tokens.Issue(result.User!), Identity(result.User!))),

                AdminLoginStatus.Locked =>
                    Results.Json(new AdminAuthErrorDto("admin.auth.locked", result.LockedUntil), statusCode: 429),

                _ => Results.Json(new AdminAuthErrorDto("admin.auth.invalid"), statusCode: 401)
            };
        });

        group.MapPost("/forgot", async (AdminForgotDto body, AdminAuthService service, HttpContext ctx) =>
        {
            var resetPage = $"{ctx.Request.Scheme}://{ctx.Request.Host}/admin/reset";
            var devLink = await service.RequestResetAsync(body.UsernameOrEmail ?? "", resetPage);

            // Always the same answer: whether the account exists is not ours to disclose.
            return Results.Ok(new AdminForgotSentDto(true, devLink));
        });

        group.MapPost("/reset", async (AdminResetDto body, AdminAuthService service) =>
        {
            var result = await service.ResetAsync(body.Token ?? "", body.NewPassword ?? "");
            return Answer(result);
        });

        // Everything below needs a valid admin session.
        var authed = app.MapGroup("/api/admin/auth").RequireAuthorization("admin");

        authed.MapGet("/me", async (HttpContext ctx, IAdminUserRepository admins) =>
            await admins.GetAsync(ctx.User.AdminId()!) is { } user
                ? Results.Ok(Identity(user))
                : Results.Unauthorized());

        authed.MapPost("/password", async (AdminChangePasswordDto body, HttpContext ctx, AdminAuthService service) =>
            Answer(await service.ChangePasswordAsync(ctx.User.AdminId()!, body.CurrentPassword ?? "", body.NewPassword ?? "")));

        authed.MapPost("/email", async (AdminEmailDto body, HttpContext ctx, IAdminUserRepository admins) =>
        {
            if (!EmailAddress.LooksValid(body.Email)) return Results.BadRequest(new AdminAuthErrorDto("admin.auth.badEmail"));

            var mine = ctx.User.AdminId()!;
            if (await admins.GetByEmailAsync(body.Email!) is { } other && other.Id != mine)
                return Results.BadRequest(new AdminAuthErrorDto("admin.auth.emailTaken"));

            if (await admins.GetAsync(mine) is not { } user) return Results.Unauthorized();

            user.ChangeEmail(body.Email!);
            await admins.UpsertAsync(user);

            return Results.Ok(Identity(user));
        });
    }

    private static IResult Answer(PasswordChangeResult result) => result.Status switch
    {
        PasswordChangeStatus.Ok => Results.Ok(),
        PasswordChangeStatus.WeakPassword => Results.Json(new AdminAuthErrorDto("admin.auth.weak", null, [.. result.Problems]), statusCode: 400),
        PasswordChangeStatus.WrongCurrentPassword => Results.Json(new AdminAuthErrorDto("admin.auth.wrongCurrent"), statusCode: 400),
        PasswordChangeStatus.ExpiredToken => Results.Json(new AdminAuthErrorDto("admin.auth.linkExpired"), statusCode: 400),
        PasswordChangeStatus.UsedToken => Results.Json(new AdminAuthErrorDto("admin.auth.linkUsed"), statusCode: 400),
        _ => Results.Json(new AdminAuthErrorDto("admin.auth.linkInvalid"), statusCode: 400)
    };

    private static AdminIdentityDto Identity(AdminUser user) => new(user.Id, user.Username, user.Email, user.MustChangePassword);

    public static string? AdminId(this ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier);
}
