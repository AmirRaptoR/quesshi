using MongoDB.Driver;
using Quesshi.Application.Ports;

namespace Quesshi.Infrastructure.Mongo;

public sealed class MongoVotingGenerationLog(MongoContext db) : IVotingGenerationLog
{
    public Task SaveAsync(VotingGenerationRun run, CancellationToken ct = default)
        => db.GenerationRuns.ReplaceOneAsync(r => r.Id == run.Id && r.Family == (int)Quesshi.Domain.QuestionFamily.Voting,
            GenerationRunDoc.From(run), new ReplaceOptions { IsUpsert = true }, ct);

    public async Task<IReadOnlyList<VotingGenerationRun>> RecentAsync(int take,
        CancellationToken ct = default)
        => [.. (await db.GenerationRuns.Find(r => r.Family == (int)Quesshi.Domain.QuestionFamily.Voting)
            .SortByDescending(r => r.StartedAt).Limit(Math.Clamp(take, 1, 100)).ToListAsync(ct))
            .Select(r => r.ToVoting())];
}
