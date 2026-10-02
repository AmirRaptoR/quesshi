using MongoDB.Driver;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Infrastructure.Mongo;

public sealed class MongoVotingCategoryRepository(MongoContext db) : IVotingCategoryRepository
{
    public async Task<IReadOnlyList<VotingCategory>> AllAsync(CancellationToken ct = default)
        => [.. (await db.VotingCategories.Find(Builders<VotingCategoryDoc>.Filter.Empty)
            .SortBy(c => c.SortOrder).ToListAsync(ct)).Select(d => d.ToDomain())];

    public async Task<VotingCategory?> GetAsync(string id, CancellationToken ct = default)
        => (await db.VotingCategories.Find(c => c.Id == id).FirstOrDefaultAsync(ct))?.ToDomain();

    public Task UpsertAsync(VotingCategory category, CancellationToken ct = default)
        => db.VotingCategories.ReplaceOneAsync(c => c.Id == category.Id, VotingCategoryDoc.From(category),
            new ReplaceOptions { IsUpsert = true }, ct);

    public Task DeleteAsync(string id, CancellationToken ct = default)
        => db.VotingCategories.DeleteOneAsync(c => c.Id == id, ct);
}
