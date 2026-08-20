using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Application.Tests;

public sealed class InMemoryAdminUsers : IAdminUserRepository
{
    public readonly List<AdminUser> Items = [];

    public Task<AdminUser?> GetAsync(string id, CancellationToken ct = default)
        => Task.FromResult(Items.FirstOrDefault(a => a.Id == id));

    public Task<AdminUser?> GetByUsernameAsync(string username, CancellationToken ct = default)
        => Task.FromResult(Items.FirstOrDefault(a => a.Username == username.Trim().ToLowerInvariant()));

    public Task<AdminUser?> GetByEmailAsync(string email, CancellationToken ct = default)
        => Task.FromResult(Items.FirstOrDefault(a => a.Email == email.Trim().ToLowerInvariant()));

    public Task<IReadOnlyList<AdminUser>> AllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AdminUser>>([.. Items]);

    public Task<long> CountAsync(CancellationToken ct = default) => Task.FromResult((long)Items.Count);

    public Task UpsertAsync(AdminUser user, CancellationToken ct = default)
    {
        Items.RemoveAll(a => a.Id == user.Id);
        Items.Add(user);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        Items.RemoveAll(a => a.Id == id);
        return Task.CompletedTask;
    }
}
