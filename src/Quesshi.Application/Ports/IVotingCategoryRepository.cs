using Quesshi.Domain;

namespace Quesshi.Application.Ports;

/// <summary>Persistence boundary for voting categories, separate from trivia categories.</summary>
public interface IVotingCategoryRepository
{
    Task<IReadOnlyList<VotingCategory>> AllAsync(CancellationToken ct = default);
    Task<VotingCategory?> GetAsync(string id, CancellationToken ct = default);
    Task UpsertAsync(VotingCategory category, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
}
