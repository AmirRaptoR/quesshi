using Quesshi.Application.Identity;
using Quesshi.Domain;
using Quesshi.Engine.Contracts;
using Quesshi.Infrastructure.Database;
using Quesshi.Infrastructure.Security;
using Quesshi.Plugin.Api;

namespace Quesshi.Infrastructure.Identity;

/// <summary>Maintains globally shared administrator identities and their tenant memberships.</summary>
internal sealed class AdminAccountManager(DatabaseSources sources, IAdminPasswordHasher passwords, IClock clock) : IAdminAccountManager
{
    private readonly ISystemDb database = new SystemDb(sources);
    public async Task<IReadOnlyList<ManagedAdminAccount>> ListAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await database.QueryAsync<ManagedAdminAccount>("""
            select a.id as Id, a.username as Username, a.email as Email, a.is_active as IsActive,
                   a.is_platform as IsPlatform, a.must_change_password as MustChangePassword,
                   a.created_at as CreatedAt, a.last_login_at as LastLoginAt, m.role as Role
            from admin_memberships m join admins a on a.id = m.admin_id
            where m.tenant_id = @tenantId
            order by a.username
            """, new { tenantId }, cancellationToken);

    public Task<AdminAccountChangeResult> CreateAsync(Guid tenantId, CreateManagedAdmin request, CancellationToken cancellationToken = default)
    {
        var username = request.Username?.Trim() ?? string.Empty;
        if (username.Length is < 3 or > 32) return Task.FromResult(new AdminAccountChangeResult(AdminAccountChangeStatus.InvalidUsername));
        if (!EmailAddress.IsValid(request.Email)) return Task.FromResult(new AdminAccountChangeResult(AdminAccountChangeStatus.InvalidEmail));
        var problems = AdminPasswordPolicy.Problems(request.Password);
        if (problems.Count > 0) return Task.FromResult(new AdminAccountChangeResult(AdminAccountChangeStatus.WeakPassword, Problems: problems));

        return database.InTransactionAsync(async transaction =>
        {
            var email = EmailAddress.Normalize(request.Email);
            await LockNewIdentityAsync(transaction, username, email, cancellationToken);
            if (await transaction.QuerySingleOrDefaultAsync<Guid?>("select id from admins where username = @username for update", new { username }, cancellationToken) is not null)
                return new AdminAccountChangeResult(AdminAccountChangeStatus.UsernameTaken);
            if (await transaction.QuerySingleOrDefaultAsync<Guid?>("select id from admins where email = @email for update", new { email }, cancellationToken) is not null)
                return new AdminAccountChangeResult(AdminAccountChangeStatus.EmailTaken);

            var id = Guid.NewGuid();
            await transaction.ExecuteAsync("""
                insert into admins (id, username, email, password_hash, is_platform, must_change_password)
                values (@id, @username, @email, @passwordHash, @isPlatform, true);
                insert into admin_memberships (tenant_id, admin_id, role) values (@tenantId, @id, 'owner')
                """, new { id, username, email, passwordHash = passwords.Hash(request.Password), isPlatform = request.IsPlatform, tenantId }, cancellationToken);
            return new AdminAccountChangeResult(AdminAccountChangeStatus.Success,
                new ManagedAdminAccount(id, username, email, true, request.IsPlatform, true, clock.Now, null, AdminRole.Owner));
        }, cancellationToken);
    }

    public Task<AdminAccountChangeResult> ChangeRoleAsync(Guid tenantId, Guid adminId, AdminRole role, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(role)) return Task.FromResult(new AdminAccountChangeResult(AdminAccountChangeStatus.InvalidRole));
        return database.InTransactionAsync(async transaction =>
        {
            await LockPlatformAsync(transaction, cancellationToken);
            await LockTenantOwnersAsync(transaction, tenantId, cancellationToken);
            var membership = await MembershipAsync(transaction, tenantId, adminId, cancellationToken);
            if (membership is null) return new AdminAccountChangeResult(AdminAccountChangeStatus.NotFound);
            if (membership.Role == AdminRole.Owner && role != AdminRole.Owner && membership.IsActive
                && !await HasAnotherActiveOwnerAsync(transaction, tenantId, adminId, cancellationToken))
                return new AdminAccountChangeResult(AdminAccountChangeStatus.LastOwner);
            await transaction.ExecuteAsync("update admin_memberships set role = @role where tenant_id = @tenantId and admin_id = @adminId", new { tenantId, adminId, role = RoleName(role) }, cancellationToken);
            return new AdminAccountChangeResult(AdminAccountChangeStatus.Success);
        }, cancellationToken);
    }

    public Task<AdminAccountChangeResult> RemoveMembershipAsync(Guid tenantId, Guid adminId, CancellationToken cancellationToken = default) =>
        database.InTransactionAsync(async transaction =>
        {
            await LockPlatformAsync(transaction, cancellationToken);
            await LockTenantOwnersAsync(transaction, tenantId, cancellationToken);
            var membership = await MembershipAsync(transaction, tenantId, adminId, cancellationToken);
            if (membership is null) return new AdminAccountChangeResult(AdminAccountChangeStatus.NotFound);
            if (membership.Role == AdminRole.Owner && membership.IsActive
                && !await HasAnotherActiveOwnerAsync(transaction, tenantId, adminId, cancellationToken))
                return new AdminAccountChangeResult(AdminAccountChangeStatus.LastOwner);
            await transaction.ExecuteAsync("delete from auth_sessions where tenant_id = @tenantId and admin_id = @adminId; delete from admin_memberships where tenant_id = @tenantId and admin_id = @adminId", new { tenantId, adminId }, cancellationToken);
            return new AdminAccountChangeResult(AdminAccountChangeStatus.Success);
        }, cancellationToken);

    public Task<AdminAccountChangeResult> SetActiveAsync(Guid actorId, Guid adminId, bool isActive, CancellationToken cancellationToken = default) =>
        ChangeGlobalAsync(actorId, adminId, isActive, remove: false, cancellationToken);

    public Task<AdminAccountChangeResult> RemoveGlobalAsync(Guid actorId, Guid adminId, CancellationToken cancellationToken = default) =>
        ChangeGlobalAsync(actorId, adminId, isActive: false, remove: true, cancellationToken);

    private Task<AdminAccountChangeResult> ChangeGlobalAsync(Guid actorId, Guid adminId, bool isActive, bool remove, CancellationToken cancellationToken) =>
        database.InTransactionAsync(async transaction =>
        {
            if (actorId == adminId) return new AdminAccountChangeResult(remove ? AdminAccountChangeStatus.CannotDeleteSelf : AdminAccountChangeStatus.CannotChangeSelf);
            await LockPlatformAsync(transaction, cancellationToken);
            var admin = await transaction.QuerySingleOrDefaultAsync<AdminRow>("""
                select id as Id, username as Username, email as Email, is_active as IsActive, is_platform as IsPlatform,
                       must_change_password as MustChangePassword, created_at as CreatedAt, last_login_at as LastLoginAt
                from admins where id = @adminId for update
                """, new { adminId }, cancellationToken);
            if (admin is null) return new AdminAccountChangeResult(AdminAccountChangeStatus.NotFound);
            var ownerTenants = await transaction.QueryAsync<Guid>("""
                select m.tenant_id from admin_memberships m where m.admin_id = @adminId and m.role = 'owner' order by m.tenant_id
                """, new { adminId }, cancellationToken);
            foreach (var tenantId in ownerTenants) await LockTenantOwnersAsync(transaction, tenantId, cancellationToken);
            if (admin.IsPlatform && admin.IsActive && (!isActive || remove)
                && !await HasAnotherPlatformAdminAsync(transaction, adminId, cancellationToken))
                return new AdminAccountChangeResult(AdminAccountChangeStatus.LastPlatformAdmin);
            if (admin.IsActive && (!isActive || remove))
            {
                foreach (var tenantId in ownerTenants)
                    if (!await HasAnotherActiveOwnerAsync(transaction, tenantId, adminId, cancellationToken))
                        return new AdminAccountChangeResult(AdminAccountChangeStatus.LastOwner);
            }
            if (remove)
            {
                await transaction.ExecuteAsync("delete from auth_sessions where admin_id = @adminId; delete from admins where id = @adminId", new { adminId }, cancellationToken);
                return new AdminAccountChangeResult(AdminAccountChangeStatus.Success);
            }
            await transaction.ExecuteAsync("update admins set is_active = @isActive where id = @adminId; delete from auth_sessions where admin_id = @adminId and not @isActive", new { adminId, isActive }, cancellationToken);
            return new AdminAccountChangeResult(AdminAccountChangeStatus.Success);
        }, cancellationToken);

    private static Task LockPlatformAsync(ISystemTx transaction, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync("select pg_advisory_xact_lock(hashtextextended('quesshi:platform-admins', 0))", cancellationToken: cancellationToken);

    private static Task LockNewIdentityAsync(ISystemTx transaction, string username, string email, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync("""
            select pg_advisory_xact_lock(hashtextextended('quesshi:admin-username:' || lower(@username), 0));
            select pg_advisory_xact_lock(hashtextextended('quesshi:admin-email:' || lower(@email), 0))
            """, new { username, email }, cancellationToken);

    private static Task LockTenantOwnersAsync(ISystemTx transaction, Guid tenantId, CancellationToken cancellationToken) =>
        transaction.ExecuteAsync("select pg_advisory_xact_lock(hashtextextended('quesshi:tenant-owners:' || @tenantId::text, 0))", new { tenantId }, cancellationToken);

    private static Task<MembershipRow?> MembershipAsync(ISystemTx transaction, Guid tenantId, Guid adminId, CancellationToken cancellationToken) =>
        transaction.QuerySingleOrDefaultAsync<MembershipRow>("""
            select m.role as Role, a.is_active as IsActive from admin_memberships m join admins a on a.id = m.admin_id
            where m.tenant_id = @tenantId and m.admin_id = @adminId for update
            """, new { tenantId, adminId }, cancellationToken);

    private static Task<bool> HasAnotherActiveOwnerAsync(ISystemTx transaction, Guid tenantId, Guid adminId, CancellationToken cancellationToken) =>
        transaction.QuerySingleOrDefaultAsync<bool>("""
            select exists(select 1 from admin_memberships m join admins a on a.id = m.admin_id
                          where m.tenant_id = @tenantId and m.role = 'owner' and m.admin_id <> @adminId and a.is_active)
            """, new { tenantId, adminId }, cancellationToken);

    private static Task<bool> HasAnotherPlatformAdminAsync(ISystemTx transaction, Guid adminId, CancellationToken cancellationToken) =>
        transaction.QuerySingleOrDefaultAsync<bool>("select exists(select 1 from admins where is_platform and is_active and id <> @adminId)", new { adminId }, cancellationToken);

    private static string RoleName(AdminRole role) => role.ToString().ToLowerInvariant();

    private sealed record MembershipRow(AdminRole Role, bool IsActive);
    private sealed record AdminRow(Guid Id, string Username, string Email, bool IsActive, bool IsPlatform, bool MustChangePassword, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt);
}
