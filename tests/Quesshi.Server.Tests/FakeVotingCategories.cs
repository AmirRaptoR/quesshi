using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Server.Tests;

public sealed class FakeVotingCategories : IVotingCategoryRepository
{
    public readonly List<VotingCategory> Items = [];
    public Task<IReadOnlyList<VotingCategory>> AllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<VotingCategory>>([.. Items]);
    public Task<VotingCategory?> GetAsync(string id, CancellationToken ct = default)
        => Task.FromResult(Items.FirstOrDefault(c => c.Id == id));
    public Task UpsertAsync(VotingCategory category, CancellationToken ct = default)
    { Items.RemoveAll(x => x.Id == category.Id); Items.Add(category); return Task.CompletedTask; }
    public Task DeleteAsync(string id, CancellationToken ct = default)
    { Items.RemoveAll(x => x.Id == id); return Task.CompletedTask; }
}
