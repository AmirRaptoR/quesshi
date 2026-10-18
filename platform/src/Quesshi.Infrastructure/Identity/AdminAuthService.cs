using System.Security.Cryptography;
using Dapper;
using Npgsql;
using Quesshi.Application.Identity;
using Quesshi.Engine.Contracts;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Security;

namespace Quesshi.Infrastructure.Identity;

internal sealed class AdminAuthService(DatabaseSources sources, IAdminPasswordHasher hasher, IAdminPasswordResetMailer mailer, IClock clock)
{
    private const int MaximumFailures = 10;
    private const int MaximumAddressAttempts = 20;
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan AddressRateLimitWindow = TimeSpan.FromMinutes(10);

    public async Task<AdminLoginResult> LoginAsync(Guid tenantId, string username, string password, string clientAddress = "unknown", CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(sources.SystemConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await RemoveExpiredAddressLimitsAsync(connection, transaction, cancellationToken);
        var addressAttempts = await IncrementAddressLimitAsync(connection, transaction, clientAddress, cancellationToken);
        if (addressAttempts > MaximumAddressAttempts)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(AdminLoginStatus.InvalidCredentials);
        }
        var admin = await connection.QuerySingleOrDefaultAsync<LoginRow>(new CommandDefinition("""
            select a.id as Id, a.password_hash as PasswordHash, a.is_active as IsActive,
                   a.must_change_password as MustChangePassword, a.locked_until as LockedUntil,
                   (a.is_platform or exists (
                       select 1 from admin_memberships m where m.admin_id = a.id and m.tenant_id = @tenantId and m.is_active
                   )) as HasTenantAccess
            from admins a where a.username = @username for update
            """, new { tenantId, username = username?.Trim() ?? string.Empty }, transaction, cancellationToken: cancellationToken));

        if (admin is null || !admin.IsActive)
        {
            hasher.BurnTime();
            await transaction.CommitAsync(cancellationToken);
            return new(AdminLoginStatus.InvalidCredentials);
        }

        if (!admin.HasTenantAccess)
        {
            hasher.Verify(admin.PasswordHash, password ?? string.Empty);
            await transaction.CommitAsync(cancellationToken);
            return new(AdminLoginStatus.InvalidCredentials);
        }

        if (admin.LockedUntil is { } lockedUntil && lockedUntil > clock.Now.UtcDateTime)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(AdminLoginStatus.Locked, LockedUntil: new DateTimeOffset(lockedUntil, TimeSpan.Zero));
        }

        var passwordIsValid = hasher.Verify(admin.PasswordHash, password ?? string.Empty);

        if (!passwordIsValid)
        {
            var locked = await connection.QuerySingleOrDefaultAsync<DateTime?>(new CommandDefinition("""
                update admins
                set failed_attempts = failed_attempts + 1,
                    locked_until = case when failed_attempts + 1 >= @maximumFailures then @now + @lockDuration else locked_until end
                where id = @id
                returning locked_until
                """, new { admin.Id, maximumFailures = MaximumFailures, now = clock.Now, lockDuration = LockDuration }, transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return locked is { } until && until > clock.Now.UtcDateTime
                ? new(AdminLoginStatus.Locked, LockedUntil: new DateTimeOffset(until, TimeSpan.Zero))
                : new(AdminLoginStatus.InvalidCredentials);
        }

        await connection.ExecuteAsync(new CommandDefinition("""
            update admins set failed_attempts = 0, locked_until = null, last_login_at = @now where id = @id
            """, new { admin.Id, now = clock.Now }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return new(AdminLoginStatus.Success, admin.Id, admin.MustChangePassword);
    }

    public async Task RequestResetAsync(string usernameOrEmail, string resetPageUrl, CancellationToken cancellationToken = default)
    {
        var handle = usernameOrEmail?.Trim() ?? string.Empty;
        await using var connection = new NpgsqlConnection(sources.SystemConnectionString);
        await connection.OpenAsync(cancellationToken);
        var admin = await connection.QuerySingleOrDefaultAsync<ResetRecipient>(new CommandDefinition("""
            select id as Id, username as Username, email as Email
            from admins where is_active and (username = @handle or email = @handle)
            order by case when username = @handle then 0 else 1 end
            limit 1
            """, new { handle }, cancellationToken: cancellationToken));
        if (admin is null) return;

        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var hash = SHA256.HashData(Convert.FromBase64String(secret));
        await connection.ExecuteAsync(new CommandDefinition("""
            insert into admin_password_resets (token_hash, admin_id, expires_at) values (@hash, @adminId, now() + interval '1 hour')
            """, new { hash, adminId = admin.Id }, cancellationToken: cancellationToken));
        var separator = resetPageUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        await mailer.SendAsync(admin.Email, admin.Username, $"{resetPageUrl}{separator}token={Uri.EscapeDataString(secret)}", cancellationToken);
    }

    public async Task<AdminPasswordChangeResult> ResetAsync(string token, string newPassword, CancellationToken cancellationToken = default)
    {
        if (!TryHashToken(token, out var hash)) return new(AdminPasswordChangeStatus.InvalidToken);
        await using var connection = new NpgsqlConnection(sources.SystemConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var reset = await connection.QuerySingleOrDefaultAsync<ResetRow>(new CommandDefinition("""
            select admin_id as AdminId, expires_at as ExpiresAt, used_at as UsedAt
            from admin_password_resets where token_hash = @hash for update
            """, new { hash }, transaction, cancellationToken: cancellationToken));
        if (reset is null) return await FinishAsync(new(AdminPasswordChangeStatus.InvalidToken));
        if (reset.UsedAt is not null) return await FinishAsync(new(AdminPasswordChangeStatus.UsedToken));
        if (reset.ExpiresAt <= clock.Now.UtcDateTime) return await FinishAsync(new(AdminPasswordChangeStatus.ExpiredToken));
        var problems = AdminPasswordPolicy.Problems(newPassword);
        if (problems.Count > 0) return await FinishAsync(new(AdminPasswordChangeStatus.WeakPassword, problems));

        var updated = await connection.ExecuteAsync(new CommandDefinition("""
            update admins set password_hash = @passwordHash, must_change_password = false, failed_attempts = 0, locked_until = null
            where id = @adminId and is_active
            """, new { passwordHash = hasher.Hash(newPassword), adminId = reset.AdminId }, transaction, cancellationToken: cancellationToken));
        if (updated == 0) return await FinishAsync(new(AdminPasswordChangeStatus.NoSuchAccount));
        await connection.ExecuteAsync(new CommandDefinition("update admin_password_resets set used_at = @now where token_hash = @hash", new { hash, now = clock.Now }, transaction, cancellationToken: cancellationToken));
        return await FinishAsync(AdminPasswordChangeResult.Success);

        async Task<AdminPasswordChangeResult> FinishAsync(AdminPasswordChangeResult result)
        {
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
    }

    public async Task<AdminPasswordChangeResult> ChangePasswordAsync(Guid adminId, string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(sources.SystemConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var admin = await connection.QuerySingleOrDefaultAsync<LoginRow>(new CommandDefinition("""
            select id as Id, password_hash as PasswordHash, is_active as IsActive, must_change_password as MustChangePassword,
                   locked_until as LockedUntil, true as HasTenantAccess from admins where id = @adminId for update
            """, new { adminId }, transaction, cancellationToken: cancellationToken));
        if (admin is null || !admin.IsActive) return await FinishAsync(new(AdminPasswordChangeStatus.NoSuchAccount));
        if (!hasher.Verify(admin.PasswordHash, currentPassword ?? string.Empty)) return await FinishAsync(new(AdminPasswordChangeStatus.WrongCurrentPassword));
        var problems = AdminPasswordPolicy.Problems(newPassword);
        if (problems.Count > 0) return await FinishAsync(new(AdminPasswordChangeStatus.WeakPassword, problems));
        await connection.ExecuteAsync(new CommandDefinition("""
            update admins set password_hash = @passwordHash, must_change_password = false, failed_attempts = 0, locked_until = null where id = @adminId
            """, new { passwordHash = hasher.Hash(newPassword), adminId }, transaction, cancellationToken: cancellationToken));
        return await FinishAsync(AdminPasswordChangeResult.Success);

        async Task<AdminPasswordChangeResult> FinishAsync(AdminPasswordChangeResult result)
        {
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
    }

    private static bool TryHashToken(string? token, out byte[] hash)
    {
        hash = [];
        try
        {
            var bytes = Convert.FromBase64String(token ?? string.Empty);
            if (bytes.Length != 32) return false;
            hash = SHA256.HashData(bytes);
            return true;
        }
        catch (FormatException) { return false; }
    }

    private static Task RemoveExpiredAddressLimitsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition("""
            delete from auth_rate_limits
            where key like 'admin-ip:%' and window_start < now() - @rateLimitWindow
            """, new { rateLimitWindow = AddressRateLimitWindow }, transaction, cancellationToken: cancellationToken));

    private static Task<int> IncrementAddressLimitAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string? clientAddress, CancellationToken cancellationToken) =>
        connection.QuerySingleAsync<int>(new CommandDefinition("""
            insert into auth_rate_limits (key, window_start, count)
            values (@key, to_timestamp(floor(extract(epoch from now()) / @seconds) * @seconds), 1)
            on conflict (key, window_start) do update set count = auth_rate_limits.count + 1
            returning count
            """, new
        {
            key = $"admin-ip:{(string.IsNullOrWhiteSpace(clientAddress) ? "unknown" : clientAddress.Trim())}",
            seconds = (int)AddressRateLimitWindow.TotalSeconds
        }, transaction, cancellationToken: cancellationToken));

    private sealed record LoginRow(Guid Id, string PasswordHash, bool IsActive, bool MustChangePassword, DateTime? LockedUntil, bool HasTenantAccess);
    private sealed record ResetRecipient(Guid Id, string Username, string Email);
    private sealed record ResetRow(Guid AdminId, DateTime ExpiresAt, DateTime? UsedAt);
}
