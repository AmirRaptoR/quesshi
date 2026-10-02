using Microsoft.Extensions.Logging.Abstractions;
using Quesshi.Application.Ports;
using Quesshi.Application.UseCases;
using Quesshi.Domain;
using Quesshi.Infrastructure;
using Quesshi.Infrastructure.Security;
using Quesshi.Server.Auth;
using Quesshi.Server.Tenants;

namespace Quesshi.Server.Tests;

public sealed class TenantAdminBootstrapperTests
{
    [Fact]
    public async Task First_admin_is_created_in_each_tenant_repository_partition()
    {
        var tenant = new TenantContext();
        var admins = new TenantAdminRepository(tenant);
        var auth = new AdminAuthService(admins, null!, new IdentityPasswordHasher(), null!, Shared.Clock, new IdFactory());
        var options = new AdminAuthOptions { BootstrapPassword = "A strong password 123!" };

        await TenantAdminBootstrapper.EnsureFirstAdminAsync("quesshi", tenant, admins, auth, options, NullLogger.Instance);
        await TenantAdminBootstrapper.EnsureFirstAdminAsync("quessher", tenant, admins, auth, options, NullLogger.Instance);

        using (tenant.Enter("quesshi")) Assert.Equal(1, await admins.CountAsync());
        using (tenant.Enter("quessher")) Assert.Equal(1, await admins.CountAsync());
        Assert.Equal("quesshi", tenant.Id);
    }

    private sealed class TenantAdminRepository(TenantContext tenant) : IAdminUserRepository
    {
        private readonly Dictionary<string, Dictionary<string, AdminUser>> _users = new(StringComparer.Ordinal);
        private Dictionary<string, AdminUser> Current
        {
            get
            {
                if (!_users.TryGetValue(tenant.Id, out var users))
                    _users.Add(tenant.Id, users = new(StringComparer.Ordinal));
                return users;
            }
        }

        public Task<AdminUser?> GetAsync(string id, CancellationToken ct = default)
            => Task.FromResult(Current.GetValueOrDefault(id));
        public Task<AdminUser?> GetByUsernameAsync(string username, CancellationToken ct = default)
            => Task.FromResult(Current.Values.FirstOrDefault(user => user.Username == username));
        public Task<AdminUser?> GetByEmailAsync(string email, CancellationToken ct = default)
            => Task.FromResult(Current.Values.FirstOrDefault(user => user.Email == email));
        public Task<IReadOnlyList<AdminUser>> AllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AdminUser>>(Current.Values.ToArray());
        public Task<long> CountAsync(CancellationToken ct = default) => Task.FromResult((long)Current.Count);
        public Task UpsertAsync(AdminUser user, CancellationToken ct = default)
        {
            Current[user.Id] = user;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string id, CancellationToken ct = default)
        {
            Current.Remove(id);
            return Task.CompletedTask;
        }
    }
}
