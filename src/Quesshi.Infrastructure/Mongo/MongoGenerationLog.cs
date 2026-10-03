using MongoDB.Driver;
using Quesshi.Application.Ports;

namespace Quesshi.Infrastructure.Mongo;

public sealed class MongoGenerationLog(MongoContext db) : IGenerationLog
{
    public Task SaveAsync(GenerationRun run, CancellationToken ct = default)
        => db.GenerationRuns.ReplaceOneAsync(r => r.Id == run.Id && r.Family == (int)run.Family,
            GenerationRunDoc.From(run), new ReplaceOptions { IsUpsert = true }, ct);

    public async Task<IReadOnlyList<GenerationRun>> RecentAsync(int take, CancellationToken ct = default)
        => [.. (await db.GenerationRuns.Find(r => r.Family == (int)Quesshi.Domain.QuestionFamily.Trivia)
            .SortByDescending(r => r.StartedAt).Limit(take).ToListAsync(ct)).Select(d => d.ToDomain())];
}
