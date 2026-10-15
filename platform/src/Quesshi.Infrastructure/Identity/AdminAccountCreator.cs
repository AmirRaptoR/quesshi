using Dapper;
using Npgsql;
using Quesshi.Application.Identity;
using Quesshi.Domain;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Security;

namespace Quesshi.Infrastructure.Identity;

internal enum AdminAccountCreationStatus
{
    Created,
    InvalidUsername,
    InvalidEmail,
    WeakPassword,
    TenantNotFound,
    TenantAlreadyHasAdmin,
    DuplicateIdentity
}

internal sealed record AdminAccountCreationResult(
    AdminAccountCreationStatus Status,
    Guid? AdminId = null,
    IReadOnlyList<string>? PasswordProblems = null);

internal sealed record AdminAccountRequest(
    string Username,
    string Email,
    string Password,
    bool IsPlatform,
    bool MustChangePassword,
    string? TenantSlug = null,
    Guid? TenantId = null,
    bool OnlyIfTenantHasNoAdmin = false);

/// <summary>Creates global admin accounts and optional tenant owner memberships atomically.</summary>
internal sealed class AdminAccountCreator(ISystemDb database, IAdminPasswordHasher hasher)
{
    public async Task<AdminAccountCreationResult> CreateAsync(AdminAccountRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var username = request.Username?.Trim() ?? string.Empty;
        var email = EmailAddress.Normalize(request.Email ?? string.Empty);
        if (username.Length == 0) return new(AdminAccountCreationStatus.InvalidUsername);
        if (!EmailAddress.IsValid(email)) return new(AdminAccountCreationStatus.InvalidEmail);
        var passwordProblems = AdminPasswordPolicy.Problems(request.Password);
        if (passwordProblems.Count > 0) return new(AdminAccountCreationStatus.WeakPassword, PasswordProblems: passwordProblems);
        if (request.TenantSlug is not null && request.TenantId is not null)
        {
            throw new ArgumentException("Specify a tenant slug or tenant id, not both.", nameof(request));
        }

        try
        {
            return await database.InTransactionAsync(async transaction =>
            {
                var tenantId = await ResolveTenantAsync(transaction, request, cancellationToken);
                if ((request.TenantSlug is not null || request.TenantId is not null) && tenantId is null)
                {
                    return new AdminAccountCreationResult(AdminAccountCreationStatus.TenantNotFound);
                }

                if (request.OnlyIfTenantHasNoAdmin && tenantId is { } bootstrapTenantId)
                {
                    var existingAdminId = await transaction.QuerySingleOrDefaultAsync<Guid?>("""
                        select admin_id from admin_memberships where tenant_id = @tenantId limit 1
                        """, new { tenantId = bootstrapTenantId }, cancellationToken);
                    if (existingAdminId is not null)
                    {
                        return new AdminAccountCreationResult(AdminAccountCreationStatus.TenantAlreadyHasAdmin);
                    }
                }

                var existing = await transaction.QueryAsync<ExistingIdentity>("""
                    select username = @username as UsernameTaken, email = @email as EmailTaken
                    from admins
                    where username = @username or email = @email
                    for update
                    """, new { username, email }, cancellationToken);
                if (existing.Any(identity => identity.UsernameTaken || identity.EmailTaken))
                {
                    return new AdminAccountCreationResult(AdminAccountCreationStatus.DuplicateIdentity);
                }

                var adminId = Guid.NewGuid();
                await transaction.ExecuteAsync("""
                    insert into admins (id, username, email, password_hash, is_active, is_platform, must_change_password)
                    values (@adminId, @username, @email, @passwordHash, true, @isPlatform, @mustChangePassword)
                    """, new
                {
                    adminId,
                    username,
                    email,
                    passwordHash = hasher.Hash(request.Password),
                    request.IsPlatform,
                    request.MustChangePassword
                }, cancellationToken);

                if (tenantId is { } membershipTenantId)
                {
                    await transaction.ExecuteAsync("""
                        insert into admin_memberships (tenant_id, admin_id, role)
                        values (@tenantId, @adminId, 'owner')
                        """, new { tenantId = membershipTenantId, adminId }, cancellationToken);
                }

                return new AdminAccountCreationResult(AdminAccountCreationStatus.Created, adminId);
            }, cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return new(AdminAccountCreationStatus.DuplicateIdentity);
        }
    }

    private static async Task<Guid?> ResolveTenantAsync(ISystemTx transaction, AdminAccountRequest request, CancellationToken cancellationToken)
    {
        if (request.TenantSlug is not null)
        {
            var tenant = await transaction.QuerySingleOrDefaultAsync<TenantRow>("""
                select id as Id from tenants where slug = @slug for update
                """, new { slug = request.TenantSlug }, cancellationToken);
            return tenant?.Id;
        }

        if (request.TenantId is not { } tenantId) return null;
        var tenantById = await transaction.QuerySingleOrDefaultAsync<TenantRow>("""
            select id as Id from tenants where id = @tenantId for update
            """, new { tenantId }, cancellationToken);
        return tenantById?.Id;
    }

    private sealed record ExistingIdentity(bool UsernameTaken, bool EmailTaken);
    private sealed record TenantRow(Guid Id);
}
