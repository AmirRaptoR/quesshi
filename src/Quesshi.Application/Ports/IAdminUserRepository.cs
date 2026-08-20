using Quesshi.Domain;

namespace Quesshi.Application.Ports;

public interface IAdminUserRepository
{
    Task<AdminUser?> GetAsync(string id, CancellationToken ct = default);
    Task<AdminUser?> GetByUsernameAsync(string username, CancellationToken ct = default);
    Task<AdminUser?> GetByEmailAsync(string email, CancellationToken ct = default);
    Task<IReadOnlyList<AdminUser>> AllAsync(CancellationToken ct = default);
    Task<long> CountAsync(CancellationToken ct = default);
    Task UpsertAsync(AdminUser user, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
}
