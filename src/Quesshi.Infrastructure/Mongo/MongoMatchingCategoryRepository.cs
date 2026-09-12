using MongoDB.Driver;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Infrastructure.Mongo;

public sealed class MongoMatchingCategoryRepository(MongoContext db) : IMatchingCategoryRepository
{
    public async Task<IReadOnlyList<MatchingCategory>> AllAsync(CancellationToken ct = default)
        => [.. (await db.MatchingCategories.Find(Builders<MatchingCategoryDoc>.Filter.Empty)
            .SortBy(c => c.SortOrder).ToListAsync(ct)).Select(d => d.ToDomain())];

    public async Task<MatchingCategory?> GetAsync(string id, CancellationToken ct = default)
        => (await db.MatchingCategories.Find(c => c.Id == id).FirstOrDefaultAsync(ct))?.ToDomain();

    public Task UpsertAsync(MatchingCategory category, CancellationToken ct = default)
        => db.MatchingCategories.ReplaceOneAsync(c => c.Id == category.Id, MatchingCategoryDoc.From(category),
            new ReplaceOptions { IsUpsert = true }, ct);

    public Task DeleteAsync(string id, CancellationToken ct = default)
        => db.MatchingCategories.DeleteOneAsync(c => c.Id == id, ct);
}
