using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Application.Tests;

public sealed class InMemoryCategories : ICategoryRepository
{
    public readonly List<Category> Items = [];
    public Task<IReadOnlyList<Category>> AllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Category>>([.. Items]);
    public Task<Category?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(Items.FirstOrDefault(c => c.Id == id));
    public Task UpsertAsync(Category c, CancellationToken ct = default) { Items.RemoveAll(x => x.Id == c.Id); Items.Add(c); return Task.CompletedTask; }
    public Task DeleteAsync(string id, CancellationToken ct = default) { Items.RemoveAll(x => x.Id == id); return Task.CompletedTask; }
}
