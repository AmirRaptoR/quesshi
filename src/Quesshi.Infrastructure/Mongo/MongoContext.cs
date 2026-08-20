using MongoDB.Driver;

namespace Quesshi.Infrastructure.Mongo;

public sealed class MongoContext
{
    public MongoContext(MongoOptions options)
    {
        var db = new MongoClient(options.ConnectionString).GetDatabase(options.Database);
        Questions = db.GetCollection<QuestionDoc>("questions");
        Categories = db.GetCollection<CategoryDoc>("categories");
        Players = db.GetCollection<PlayerDoc>("players");
        Matches = db.GetCollection<MatchDoc>("matches");
        GenerationRuns = db.GetCollection<GenerationRunDoc>("generation_runs");
        AdminUsers = db.GetCollection<AdminUserDoc>("admin_users");
        AiCalls = db.GetCollection<AiCallDoc>("ai_calls");
    }

    public IMongoCollection<QuestionDoc> Questions { get; }
    public IMongoCollection<CategoryDoc> Categories { get; }
    public IMongoCollection<PlayerDoc> Players { get; }
    public IMongoCollection<MatchDoc> Matches { get; }
    public IMongoCollection<GenerationRunDoc> GenerationRuns { get; }
    public IMongoCollection<AdminUserDoc> AdminUsers { get; }
    public IMongoCollection<AiCallDoc> AiCalls { get; }

    /// <summary>Indexes the queries the app actually makes: bucket sampling, email lookup, match history.</summary>
    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        await Questions.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<QuestionDoc>(Builders<QuestionDoc>.IndexKeys
                .Ascending(q => q.Status).Ascending(q => q.Lang).Ascending(q => q.CategoryId).Ascending(q => q.Level)),
            new CreateIndexModel<QuestionDoc>(Builders<QuestionDoc>.IndexKeys.Ascending(q => q.CategoryId)),
            new CreateIndexModel<QuestionDoc>(Builders<QuestionDoc>.IndexKeys.Descending(q => q.ReportCount)),

            // The same subject and aspect may exist once per language and no more — this is what
            // actually stops duplicate questions, rather than any check in application code. It is
            // partial on purpose: the hand-written seed bank carries no topic, and null is not a
            // value that can be unique.
            new CreateIndexModel<QuestionDoc>(
                Builders<QuestionDoc>.IndexKeys.Ascending(q => q.Lang).Ascending(q => q.Topic),
                new CreateIndexOptions<QuestionDoc>
                {
                    Unique = true,
                    PartialFilterExpression = Builders<QuestionDoc>.Filter.Type(q => q.Topic, MongoDB.Bson.BsonType.String)
                })
        ], ct);

        await Players.Indexes.CreateOneAsync(
            new CreateIndexModel<PlayerDoc>(Builders<PlayerDoc>.IndexKeys.Ascending(p => p.Email),
                new CreateIndexOptions { Unique = true }), cancellationToken: ct);

        await AdminUsers.Indexes.CreateOneAsync(
            new CreateIndexModel<AdminUserDoc>(Builders<AdminUserDoc>.IndexKeys.Ascending(a => a.Username),
                new CreateIndexOptions { Unique = true }), cancellationToken: ct);

        // The spend panel only ever asks "since when", so one index on the timestamp covers it.
        await AiCalls.Indexes.CreateOneAsync(
            new CreateIndexModel<AiCallDoc>(Builders<AiCallDoc>.IndexKeys.Descending(c => c.At)), cancellationToken: ct);

        await Matches.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<MatchDoc>(Builders<MatchDoc>.IndexKeys.Ascending(m => m.ChallengerId).Descending(m => m.CreatedAt)),
            new CreateIndexModel<MatchDoc>(Builders<MatchDoc>.IndexKeys.Ascending(m => m.OpponentId).Descending(m => m.CreatedAt))
        ], ct);
    }
}
