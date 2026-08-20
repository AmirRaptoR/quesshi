using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Application.UseCases;

/// <summary>
/// Username and password sign-in for administrators, with lockout and password reset.
///
/// Two rules run through all of it. First, nothing here ever reveals whether an account exists —
/// a wrong password, an unknown username and a deactivated account all answer the same way, and
/// all cost the same time. Second, every failure is counted, because throttling is the only thing
/// between a password and a fast guesser.
/// </summary>
public sealed class AdminAuthService(
    IAdminUserRepository admins,
    IResetTokenStore tokens,
    IPasswordHasher hasher,
    IAdminMailer mailer,
    IClock clock,
    IIdFactory ids)
{
    public async Task<AdminLoginResult> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        var user = await admins.GetByUsernameAsync(username ?? string.Empty, ct);

        if (user is null || !user.IsActive)
        {
            // Do the work anyway: an unknown username must not answer faster than a known one.
            hasher.BurnTime();
            return new AdminLoginResult(AdminLoginStatus.InvalidCredentials, null);
        }

        if (user.IsLocked(clock.Now))
            return new AdminLoginResult(AdminLoginStatus.Locked, null, user.LockedUntil);

        if (!hasher.Verify(user.PasswordHash, password ?? string.Empty))
        {
            user.RecordFailure(clock.Now);
            await admins.UpsertAsync(user, ct);

            return user.IsLocked(clock.Now)
                ? new AdminLoginResult(AdminLoginStatus.Locked, null, user.LockedUntil)
                : new AdminLoginResult(AdminLoginStatus.InvalidCredentials, null);
        }

        user.RecordSuccess(clock.Now);
        await admins.UpsertAsync(user, ct);

        return new AdminLoginResult(
            user.MustChangePassword ? AdminLoginStatus.MustChangePassword : AdminLoginStatus.Ok, user);
    }

    /// <summary>
    /// Always appears to succeed. Returns the link only when the mailer is a development stub.
    /// </summary>
    public async Task<string?> RequestResetAsync(string usernameOrEmail, string resetPageUrl, CancellationToken ct = default)
    {
        var handle = usernameOrEmail?.Trim() ?? string.Empty;

        var user = await admins.GetByUsernameAsync(handle, ct) ?? await admins.GetByEmailAsync(handle, ct);
        if (user is null || !user.IsActive) return null;

        var secret = PasswordResetToken.NewSecret();
        await tokens.SaveAsync(PasswordResetToken.Issue(user.Id, secret, clock.Now), ct);

        var separator = resetPageUrl.Contains('?') ? '&' : '?';
        return await mailer.SendPasswordResetAsync(user.Email, user.Username, $"{resetPageUrl}{separator}token={secret}", ct);
    }

    public async Task<PasswordChangeResult> ResetAsync(string secret, string newPassword, CancellationToken ct = default)
    {
        var hash = PasswordResetToken.Hash(secret ?? string.Empty);

        if (await tokens.GetAsync(hash, ct) is not { } token)
            return PasswordChangeResult.Fail(PasswordChangeStatus.InvalidToken);

        var outcome = token.Check(secret!, clock.Now);
        if (outcome != ResetResult.Ok)
            return PasswordChangeResult.Fail(outcome switch
            {
                ResetResult.Expired => PasswordChangeStatus.ExpiredToken,
                ResetResult.AlreadyUsed => PasswordChangeStatus.UsedToken,
                _ => PasswordChangeStatus.InvalidToken
            });

        // Judge the new password before spending the token: a typo should not cost the user their link.
        var problems = PasswordPolicy.Problems(newPassword);
        if (problems.Count > 0) return PasswordChangeResult.Weak(problems);

        if (await admins.GetAsync(token.AdminUserId, ct) is not { } user)
            return PasswordChangeResult.Fail(PasswordChangeStatus.NoSuchAccount);

        token.Redeem(secret!, clock.Now);
        user.SetPassword(hasher.Hash(newPassword), clock.Now);

        await admins.UpsertAsync(user, ct);

        // Kept, not deleted: a second click on the same link should be told it was already used.
        // The store expires it on its own.
        await tokens.SaveAsync(token, ct);

        return PasswordChangeResult.Ok;
    }

    public async Task<PasswordChangeResult> ChangePasswordAsync(string adminUserId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        if (await admins.GetAsync(adminUserId, ct) is not { } user)
            return PasswordChangeResult.Fail(PasswordChangeStatus.NoSuchAccount);

        if (!hasher.Verify(user.PasswordHash, currentPassword ?? string.Empty))
            return PasswordChangeResult.Fail(PasswordChangeStatus.WrongCurrentPassword);

        var problems = PasswordPolicy.Problems(newPassword);
        if (problems.Count > 0) return PasswordChangeResult.Weak(problems);

        user.SetPassword(hasher.Hash(newPassword), clock.Now);
        await admins.UpsertAsync(user, ct);

        return PasswordChangeResult.Ok;
    }

    /// <summary>Creates an administrator. Used by the bootstrap on an empty install and by the admin panel.</summary>
    public async Task<AdminUser> CreateAsync(string username, string email, string password, bool mustChangePassword, CancellationToken ct = default)
    {
        var user = AdminUser.Create(ids.NewId(), username, email, hasher.Hash(password), clock.Now, mustChangePassword);
        await admins.UpsertAsync(user, ct);
        return user;
    }
}
