using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Application.Tests;

public sealed class InMemoryMatchingCategories : IMatchingCategoryRepository
{
    public readonly List<MatchingCategory> Items = [];
    public Task<IReadOnlyList<MatchingCategory>> AllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MatchingCategory>>([.. Items]);
    public Task<MatchingCategory?> GetAsync(string id, CancellationToken ct = default)
        => Task.FromResult(Items.FirstOrDefault(c => c.Id == id));
    public Task UpsertAsync(MatchingCategory category, CancellationToken ct = default)
    { Items.RemoveAll(x => x.Id == category.Id); Items.Add(category); return Task.CompletedTask; }
    public Task DeleteAsync(string id, CancellationToken ct = default)
    { Items.RemoveAll(x => x.Id == id); return Task.CompletedTask; }
}
