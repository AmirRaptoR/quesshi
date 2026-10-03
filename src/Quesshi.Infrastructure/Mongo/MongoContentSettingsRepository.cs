using MongoDB.Driver;
using Quesshi.Application.Ports;

namespace Quesshi.Infrastructure.Mongo;

public sealed class MongoContentSettingsRepository(MongoContext db) : IContentSettingsRepository
{
    public async Task<ContentSettings> GetAsync(CancellationToken ct = default)
        => (await db.ContentSettings.Find(x => x.Id == "settings").FirstOrDefaultAsync(ct))?.ToDomain()
            ?? ContentSettings.Empty;

    public async Task<ContentSettings> GetOrCreateAsync(ContentSettings initial, CancellationToken ct = default)
    {
        var update = Builders<ContentSettingsDoc>.Update
            .SetOnInsert(x => x.TriviaCategoryIds, [.. initial.TriviaCategoryIds])
            .SetOnInsert(x => x.VotingCategoryIds, [.. initial.VotingCategoryIds]);
        var options = new FindOneAndUpdateOptions<ContentSettingsDoc> { IsUpsert = true, ReturnDocument = ReturnDocument.After };
        return (await db.ContentSettings.FindOneAndUpdateAsync(x => x.Id == "settings", update, options, ct)).ToDomain();
    }

    public Task SaveAsync(ContentSettings settings, CancellationToken ct = default)
        => db.ContentSettings.ReplaceOneAsync(x => x.Id == "settings", ContentSettingsDoc.From(settings),
            new ReplaceOptions { IsUpsert = true }, ct);
}
