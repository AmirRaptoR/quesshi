using MongoDB.Driver;
using Quesshi.Application.Ports;

namespace Quesshi.Infrastructure.Mongo;

public sealed class MongoVotingGenerationLog(MongoContext db) : IVotingGenerationLog
{
    public Task SaveAsync(VotingGenerationRun run, CancellationToken ct = default)
        => db.VotingGenerationRuns.ReplaceOneAsync(r => r.Id == run.Id,
            VotingGenerationRunDoc.From(run), new ReplaceOptions { IsUpsert = true }, ct);

    public async Task<IReadOnlyList<VotingGenerationRun>> RecentAsync(int take,
        CancellationToken ct = default)
        => [.. (await db.VotingGenerationRuns.Find(Builders<VotingGenerationRunDoc>.Filter.Empty)
            .SortByDescending(r => r.StartedAt).Limit(Math.Clamp(take, 1, 100)).ToListAsync(ct))
            .Select(r => r.ToDomain())];
}
