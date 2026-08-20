using Quesshi.Domain;

namespace Quesshi.Application.Ports;

public interface ICategoryRepository
{
    Task<IReadOnlyList<Category>> AllAsync(CancellationToken ct = default);
    Task<Category?> GetAsync(string id, CancellationToken ct = default);
    Task UpsertAsync(Category category, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
}
