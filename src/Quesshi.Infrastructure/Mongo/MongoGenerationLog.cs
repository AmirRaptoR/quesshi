using MongoDB.Driver;
using Quesshi.Application.Ports;

namespace Quesshi.Infrastructure.Mongo;

public sealed class MongoGenerationLog(MongoContext db) : IGenerationLog
{
    public Task SaveAsync(GenerationRun run, CancellationToken ct = default)
        => db.GenerationRuns.ReplaceOneAsync(r => r.Id == run.Id, GenerationRunDoc.From(run), new ReplaceOptions { IsUpsert = true }, ct);

    public async Task<IReadOnlyList<GenerationRun>> RecentAsync(int take, CancellationToken ct = default)
        => [.. (await db.GenerationRuns.Find(Builders<GenerationRunDoc>.Filter.Empty)
            .SortByDescending(r => r.StartedAt).Limit(take).ToListAsync(ct)).Select(d => d.ToDomain())];
}
