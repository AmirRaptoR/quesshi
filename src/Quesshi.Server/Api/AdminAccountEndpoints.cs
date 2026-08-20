using Quesshi.Application.Ports;
using Quesshi.Application.UseCases;
using Quesshi.Domain;
using Quesshi.Shared;

namespace Quesshi.Server.Api;

/// <summary>Managing the administrator accounts themselves.</summary>
public static class AdminAccountEndpoints
{
    public static void MapAdminAccounts(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/accounts").RequireAuthorization("admin");

        group.MapGet("/", async (IAdminUserRepository admins, IClock clock) =>
            (await admins.AllAsync()).Select(a => Describe(a, clock.Now)).ToList());

        group.MapPost("/", async (CreateAdminDto body, IAdminUserRepository admins, AdminAuthService service, IClock clock) =>
        {
            var username = (body.Username ?? "").Trim();
            if (username.Length is < 3 or > 32) return Results.BadRequest(new AdminAuthErrorDto("admin.auth.badUsername"));
            if (await admins.GetByUsernameAsync(username) is not null) return Results.BadRequest(new AdminAuthErrorDto("admin.auth.usernameTaken"));

            // The email is the only route back in after a forgotten password, so it is required
            // and must be unique — a shared address makes "forgot password" ambiguous.
            if (!EmailAddress.LooksValid(body.Email)) return Results.BadRequest(new AdminAuthErrorDto("admin.auth.badEmail"));
            if (await admins.GetByEmailAsync(body.Email!) is not null) return Results.BadRequest(new AdminAuthErrorDto("admin.auth.emailTaken"));

            var problems = PasswordPolicy.Problems(body.Password);
            if (problems.Count > 0) return Results.BadRequest(new AdminAuthErrorDto("admin.auth.weak", null, [.. problems]));

            var created = await service.CreateAsync(username, body.Email ?? "", body.Password!, mustChangePassword: true);
            return Results.Ok(Describe(created, clock.Now));
        });

        group.MapPost("/{id}/active", async (string id, bool value, HttpContext ctx, IAdminUserRepository admins, IClock clock) =>
        {
            if (id == ctx.User.AdminId()) return Results.BadRequest(new AdminAuthErrorDto("admin.auth.cannotDisableSelf"));

            if (await admins.GetAsync(id) is not { } user) return Results.NotFound();

            if (value) user.Activate(); else user.Deactivate();
            await admins.UpsertAsync(user);

            return Results.Ok(Describe(user, clock.Now));
        });

        group.MapDelete("/{id}", async (string id, HttpContext ctx, IAdminUserRepository admins) =>
        {
            if (id == ctx.User.AdminId()) return Results.BadRequest(new AdminAuthErrorDto("admin.auth.cannotDeleteSelf"));

            // Never leave the install with no way in.
            if (await admins.CountAsync() <= 1) return Results.BadRequest(new AdminAuthErrorDto("admin.auth.lastAdmin"));

            await admins.DeleteAsync(id);
            return Results.Ok();
        });
    }

    private static AdminAccountDto Describe(AdminUser user, DateTimeOffset now)
        => new(user.Id, user.Username, user.Email, user.IsActive, user.MustChangePassword,
            user.CreatedAt, user.LastLoginAt, user.IsLocked(now));
}
