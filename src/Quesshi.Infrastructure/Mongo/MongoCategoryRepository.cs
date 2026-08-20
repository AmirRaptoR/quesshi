using MongoDB.Driver;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Infrastructure.Mongo;

public sealed class MongoCategoryRepository(MongoContext db) : ICategoryRepository
{
    public async Task<IReadOnlyList<Category>> AllAsync(CancellationToken ct = default)
        => [.. (await db.Categories.Find(Builders<CategoryDoc>.Filter.Empty).SortBy(c => c.SortOrder).ToListAsync(ct)).Select(d => d.ToDomain())];

    public async Task<Category?> GetAsync(string id, CancellationToken ct = default)
        => (await db.Categories.Find(c => c.Id == id).FirstOrDefaultAsync(ct))?.ToDomain();

    public Task UpsertAsync(Category category, CancellationToken ct = default)
        => db.Categories.ReplaceOneAsync(c => c.Id == category.Id, CategoryDoc.From(category), new ReplaceOptions { IsUpsert = true }, ct);

    public Task DeleteAsync(string id, CancellationToken ct = default)
        => db.Categories.DeleteOneAsync(c => c.Id == id, ct);
}
