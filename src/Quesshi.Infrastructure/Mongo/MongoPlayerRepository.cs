using MongoDB.Bson;
using MongoDB.Driver;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Infrastructure.Mongo;

public sealed class MongoPlayerRepository(MongoContext db) : IPlayerRepository
{
    public async Task<Player?> GetAsync(string id, CancellationToken ct = default)
        => (await db.Players.Find(p => p.Id == id).FirstOrDefaultAsync(ct))?.ToDomain();

    public async Task<IReadOnlyList<Player>> GetManyAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return [];
        var found = await db.Players.Find(Builders<PlayerDoc>.Filter.In(p => p.Id, ids)).ToListAsync(ct);
        return [.. found.Select(d => d.ToDomain())];
    }

    public async Task<Player?> GetByEmailAsync(string email, CancellationToken ct = default)
    {
        var normalized = email.Trim().ToLowerInvariant();
        return (await db.Players.Find(p => p.Email == normalized).FirstOrDefaultAsync(ct))?.ToDomain();
    }

    public async Task<IReadOnlyList<Player>> SearchAsync(string? text, int skip, int take, CancellationToken ct = default)
    {
        // Guests are excluded: they cannot be signed into, friended or challenged, so offering one
        // as a search result is offering something that does not work.
        var filter = Builders<PlayerDoc>.Filter.Ne(p => p.IsGuest, true);
        if (!string.IsNullOrWhiteSpace(text))
            filter &= Builders<PlayerDoc>.Filter.Regex(p => p.DisplayName,
                new BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(text), "i"));

        return [.. (await db.Players.Find(filter).SortByDescending(p => p.CreatedAt).Skip(skip).Limit(take).ToListAsync(ct)).Select(d => d.ToDomain())];
    }

    public Task<long> CountAsync(CancellationToken ct = default)
        => db.Players.CountDocumentsAsync(Builders<PlayerDoc>.Filter.Empty, cancellationToken: ct);

    public Task UpsertAsync(Player player, CancellationToken ct = default)
        => db.Players.ReplaceOneAsync(p => p.Id == player.Id, PlayerDoc.From(player), new ReplaceOptions { IsUpsert = true }, ct);
}
