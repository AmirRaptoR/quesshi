using MongoDB.Driver;
using Quesshi.Application.Ports;

namespace Quesshi.Infrastructure.Mongo;

public sealed class MongoMatchArchive(MongoContext db) : IMatchArchive
{
    public Task SaveAsync(ArchivedMatch match, CancellationToken ct = default)
        => db.Matches.ReplaceOneAsync(m => m.Id == match.Id, MatchDoc.From(match), new ReplaceOptions { IsUpsert = true }, ct);

    public async Task<ArchivedMatch?> ByCodeAsync(string code, CancellationToken ct = default)
    {
        var normalized = code.Trim().ToUpperInvariant();
        return (await db.Matches.Find(m => m.Code == normalized).FirstOrDefaultAsync(ct))?.ToDomain();
    }

    public async Task<IReadOnlyList<ArchivedMatch>> ForPlayerAsync(string playerId, int take, CancellationToken ct = default)
        => [.. (await db.Matches
            .Find(Builders<MatchDoc>.Filter.Or(
                Builders<MatchDoc>.Filter.Eq(m => m.ChallengerId, playerId),
                Builders<MatchDoc>.Filter.Eq(m => m.OpponentId, playerId)))
            .SortByDescending(m => m.CreatedAt).Limit(take).ToListAsync(ct)).Select(d => d.ToDomain())];

    public Task<long> CountAsync(CancellationToken ct = default)
        => db.Matches.CountDocumentsAsync(Builders<MatchDoc>.Filter.Empty, cancellationToken: ct);
}
