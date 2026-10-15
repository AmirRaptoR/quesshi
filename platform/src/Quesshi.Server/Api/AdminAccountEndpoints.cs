using Quesshi.Application.Identity;
using Quesshi.Plugin.Api;
using Quesshi.Server.Security;
using Quesshi.Server.Tenants;

namespace Quesshi.Server.Api;

/// <summary>Tenant membership administration and platform-wide administrator identity controls.</summary>
public sealed class AdminAccountEndpoints : ICoreRouteModule
{
    public void Map(CoreRouteBuilder routes)
    {
        var accounts = routes.AdminRoutes(AdminRole.Owner).MapGroup("/accounts");
        accounts.MapGet("", ListAsync);
        accounts.MapPost("", CreateAsync).WithMetadata(new AdminAuditMetadata("admin.account.create", "admin"));
        accounts.MapPut("/{id:guid}/role", ChangeRoleAsync).WithMetadata(new AdminAuditMetadata("admin.membership.role", "admin"));
        accounts.MapDelete("/{id:guid}", RemoveMembershipAsync).WithMetadata(new AdminAuditMetadata("admin.membership.remove", "admin"));
        accounts.MapPost("/{id:guid}/active", SetActiveAsync).WithMetadata(new AdminAuditMetadata("admin.account.active", "admin"));
        accounts.MapDelete("/{id:guid}/global", RemoveGlobalAsync).WithMetadata(new AdminAuditMetadata("admin.account.remove", "admin"));
    }

    private static async Task<IResult> ListAsync(HttpContext context, IAdminAccountManager accounts, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        return tenant is null ? Results.NotFound() : Results.Ok(await accounts.ListAsync(tenant.Id, cancellationToken));
    }

    private static async Task<IResult> CreateAsync(HttpContext context, CreateAdminRequest request, IAdminAccountManager accounts, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        var actor = context.Admin();
        if (request.IsPlatform && actor?.IsPlatform != true) return Results.StatusCode(StatusCodes.Status403Forbidden);
        return Answer(await accounts.CreateAsync(tenant.Id, new(request.Username ?? string.Empty, request.Email ?? string.Empty, request.Password ?? string.Empty, request.IsPlatform), cancellationToken), created: true);
    }

    private static async Task<IResult> ChangeRoleAsync(HttpContext context, Guid id, ChangeAdminRoleRequest request, IAdminAccountManager accounts, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        if (!Enum.TryParse<AdminRole>(request.Role, ignoreCase: true, out var role)
            || !Enum.IsDefined(role)
            || !string.Equals(request.Role, role.ToString(), StringComparison.OrdinalIgnoreCase))
            return Error("invalid_role", StatusCodes.Status400BadRequest);
        return Answer(await accounts.ChangeRoleAsync(tenant.Id, id, role, cancellationToken));
    }

    private static async Task<IResult> RemoveMembershipAsync(HttpContext context, Guid id, IAdminAccountManager accounts, CancellationToken cancellationToken)
    {
        var tenant = TenantResolutionMiddleware.GetTenant(context);
        if (tenant is null) return Results.NotFound();
        return Answer(await accounts.RemoveMembershipAsync(tenant.Id, id, cancellationToken));
    }

    private static async Task<IResult> SetActiveAsync(HttpContext context, Guid id, bool value, IAdminAccountManager accounts, CancellationToken cancellationToken)
    {
        var admin = context.Admin();
        if (admin is null) return Results.StatusCode(StatusCodes.Status401Unauthorized);
        if (!admin.IsPlatform) return Results.StatusCode(StatusCodes.Status403Forbidden);
        return Answer(await accounts.SetActiveAsync(admin.AdminId, id, value, cancellationToken));
    }

    private static async Task<IResult> RemoveGlobalAsync(HttpContext context, Guid id, IAdminAccountManager accounts, CancellationToken cancellationToken)
    {
        var admin = context.Admin();
        if (admin is null) return Results.StatusCode(StatusCodes.Status401Unauthorized);
        if (!admin.IsPlatform) return Results.StatusCode(StatusCodes.Status403Forbidden);
        return Answer(await accounts.RemoveGlobalAsync(admin.AdminId, id, cancellationToken));
    }

    private static IResult Answer(AdminAccountChangeResult result, bool created = false) => result.Status switch
    {
        AdminAccountChangeStatus.Success when created => Results.Ok(result.Account),
        AdminAccountChangeStatus.Success => result.Account is null ? Results.NoContent() : Results.Ok(result.Account),
        AdminAccountChangeStatus.NotFound => Results.NotFound(),
        AdminAccountChangeStatus.InvalidUsername => Error("admin.auth.badUsername", StatusCodes.Status400BadRequest),
        AdminAccountChangeStatus.UsernameTaken => Error("admin.auth.usernameTaken", StatusCodes.Status400BadRequest),
        AdminAccountChangeStatus.InvalidEmail => Error("admin.auth.badEmail", StatusCodes.Status400BadRequest),
        AdminAccountChangeStatus.EmailTaken => Error("admin.auth.emailTaken", StatusCodes.Status400BadRequest),
        AdminAccountChangeStatus.WeakPassword => Results.Json(new { code = "admin.auth.weak", problems = result.Problems }, statusCode: StatusCodes.Status400BadRequest),
        AdminAccountChangeStatus.InvalidRole => Error("invalid_role", StatusCodes.Status400BadRequest),
        AdminAccountChangeStatus.LastOwner => Error("last_owner", StatusCodes.Status409Conflict),
        AdminAccountChangeStatus.LastPlatformAdmin => Error("last_platform_admin", StatusCodes.Status409Conflict),
        AdminAccountChangeStatus.CannotChangeSelf => Error("admin.auth.cannotDisableSelf", StatusCodes.Status400BadRequest),
        AdminAccountChangeStatus.CannotDeleteSelf => Error("admin.auth.cannotDeleteSelf", StatusCodes.Status400BadRequest),
        _ => Error("account_change_failed", StatusCodes.Status400BadRequest),
    };

    private static IResult Error(string code, int statusCode) => Results.Json(new { code }, statusCode: statusCode);

    private sealed record CreateAdminRequest(string? Username, string? Email, string? Password, bool IsPlatform = false);
    private sealed record ChangeAdminRoleRequest(string? Role);
}
