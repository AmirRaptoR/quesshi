using Quesshi.Domain;

namespace Quesshi.Application.Ports;

/// <summary>Persistence boundary for matching categories, separate from trivia categories.</summary>
public interface IMatchingCategoryRepository
{
    Task<IReadOnlyList<MatchingCategory>> AllAsync(CancellationToken ct = default);
    Task<MatchingCategory?> GetAsync(string id, CancellationToken ct = default);
    Task UpsertAsync(MatchingCategory category, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
}
