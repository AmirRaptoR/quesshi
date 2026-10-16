using Quesshi.Plugin.Api;

namespace Quesshi.Application.Identity;

internal sealed record ManagedAdminAccount(
    Guid Id,
    string Username,
    string Email,
    bool IsActive,
    bool IsPlatform,
    bool MustChangePassword,
    DateTimeOffset? LockedUntil,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt,
    AdminRole Role);

internal sealed record CreateManagedAdmin(string Username, string Email, string Password, bool IsPlatform);

internal enum AdminAccountChangeStatus
{
    Success,
    InvalidUsername,
    UsernameTaken,
    InvalidEmail,
    EmailTaken,
    WeakPassword,
    NotFound,
    InvalidRole,
    LastOwner,
    LastPlatformAdmin,
    CannotChangeSelf,
    CannotDeleteSelf,
}

internal sealed record AdminAccountChangeResult(AdminAccountChangeStatus Status, ManagedAdminAccount? Account = null, IReadOnlyList<string>? Problems = null);

internal interface IAdminAccountManager
{
    Task<IReadOnlyList<ManagedAdminAccount>> ListAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task<AdminAccountChangeResult> CreateAsync(Guid tenantId, CreateManagedAdmin request, CancellationToken cancellationToken = default);
    Task<AdminAccountChangeResult> ChangeRoleAsync(Guid tenantId, Guid adminId, AdminRole role, CancellationToken cancellationToken = default);
    Task<AdminAccountChangeResult> RemoveMembershipAsync(Guid tenantId, Guid adminId, CancellationToken cancellationToken = default);
    Task<AdminAccountChangeResult> SetActiveAsync(Guid actorId, Guid adminId, bool isActive, CancellationToken cancellationToken = default);
    Task<AdminAccountChangeResult> RemoveGlobalAsync(Guid actorId, Guid adminId, CancellationToken cancellationToken = default);
}
