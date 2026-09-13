using MongoDB.Driver;
using Quesshi.Application.Ports;

namespace Quesshi.Infrastructure.Mongo;

public sealed class MongoMatchingGenerationLog(MongoContext db) : IMatchingGenerationLog
{
    public Task SaveAsync(MatchingGenerationRun run, CancellationToken ct = default)
        => db.MatchingGenerationRuns.ReplaceOneAsync(r => r.Id == run.Id,
            MatchingGenerationRunDoc.From(run), new ReplaceOptions { IsUpsert = true }, ct);

    public async Task<IReadOnlyList<MatchingGenerationRun>> RecentAsync(int take,
        CancellationToken ct = default)
        => [.. (await db.MatchingGenerationRuns.Find(Builders<MatchingGenerationRunDoc>.Filter.Empty)
            .SortByDescending(r => r.StartedAt).Limit(Math.Clamp(take, 1, 100)).ToListAsync(ct))
            .Select(r => r.ToDomain())];
}
