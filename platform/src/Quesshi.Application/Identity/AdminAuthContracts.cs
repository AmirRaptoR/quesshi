namespace Quesshi.Application.Identity;

internal enum AdminLoginStatus
{
    InvalidCredentials,
    Locked,
    Success,
}

internal sealed record AdminLoginResult(AdminLoginStatus Status, Guid? AdminId = null, bool MustChangePassword = false, DateTimeOffset? LockedUntil = null);

internal enum AdminPasswordChangeStatus
{
    Success,
    WeakPassword,
    WrongCurrentPassword,
    InvalidToken,
    ExpiredToken,
    UsedToken,
    NoSuchAccount,
}

internal sealed record AdminPasswordChangeResult(AdminPasswordChangeStatus Status, IReadOnlyList<string>? Problems = null)
{
    public static AdminPasswordChangeResult Success { get; } = new(AdminPasswordChangeStatus.Success);
}

internal interface IAdminPasswordResetMailer
{
    Task SendAsync(string email, string username, string resetLink, CancellationToken cancellationToken = default);
}
