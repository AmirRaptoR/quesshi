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

        // A one-time backfill for every row written before Participants/OwnerId existed. Unlike the
        // grain snapshots in Redis this migration also tolerates — which stay permanently dual-shaped
        // because nothing ever calls ClearStateAsync and there is no safe moment to rewrite them
        // offline — this is an ordinary Mongo collection: it can be scanned and bulk-updated in place,
        // so a genuinely one-time pass is the right tool, not another forever-branch. It runs here,
        // at the same startup hook that already (idempotently) creates indexes, because this codebase
        // has no separate migration runner; the filter below means every run after the first, on an
        // already-backfilled collection, matches nothing and costs one empty query.
        await BackfillParticipantsAsync(ct);
        await DropLegacyChallengerOpponentIndexesAsync(ct);

        await Matches.Indexes.CreateManyAsync(
        [
            // One multikey index replaces the two compound ones this used to need: Participants is an
            // array, so Mongo indexes each element, and "every match with playerId somewhere in
            // Participants" is served by this single index whichever seat playerId held — owner or
            // later joiner. MongoMatchArchive.ForPlayerAsync's single AnyEq(Participants, playerId)
            // filter is what actually walks it.
            new CreateIndexModel<MatchDoc>(Builders<MatchDoc>.IndexKeys.Ascending(m => m.Participants).Descending(m => m.CreatedAt)),

            // Async and live duels share this one collection and one code namespace
            // (IIdFactory.NewMatchCode() for both), so this index — not a promise in application
            // code — is what makes a collision impossible: the database rejects the second insert
            // outright rather than letting two duels answer to the same code.
            new CreateIndexModel<MatchDoc>(Builders<MatchDoc>.IndexKeys.Ascending(m => m.Code),
                new CreateIndexOptions { Unique = true })
        ], ct);
    }

    /// <summary>
    /// Sets <see cref="MatchDoc.OwnerId"/>/<see cref="MatchDoc.Participants"/> on every row that does
    /// not have them yet — the formula is <see cref="MatchDoc.From"/>'s own: the owner is
    /// <see cref="MatchDoc.ChallengerId"/>, and Participants is that plus
    /// <see cref="MatchDoc.OpponentId"/> only when it is not null, so a lobby nobody had joined keeps
    /// its one-seat list rather than gaining a phantom second participant. A row this code has already
    /// touched (via <see cref="MatchDoc.From"/>) always has a non-empty Participants, so the filter
    /// below only ever matches what a pre-migration write actually left behind.
    /// </summary>
    private async Task BackfillParticipantsAsync(CancellationToken ct)
    {
        var needsBackfill = Builders<MatchDoc>.Filter.Or(
            Builders<MatchDoc>.Filter.Exists(m => m.Participants, exists: false),
            Builders<MatchDoc>.Filter.Size(m => m.Participants, 0));

        using var cursor = await Matches.FindAsync(needsBackfill, cancellationToken: ct);
        var writes = new List<WriteModel<MatchDoc>>();

        while (await cursor.MoveNextAsync(ct))
        {
            foreach (var doc in cursor.Current)
            {
                List<string> participants = doc.OpponentId is null ? [doc.ChallengerId] : [doc.ChallengerId, doc.OpponentId];
                var update = Builders<MatchDoc>.Update
                    .Set(m => m.OwnerId, doc.ChallengerId)
                    .Set(m => m.Participants, participants);
                writes.Add(new UpdateOneModel<MatchDoc>(Builders<MatchDoc>.Filter.Eq(m => m.Id, doc.Id), update));
            }
        }

        if (writes.Count > 0) await Matches.BulkWriteAsync(writes, cancellationToken: ct);
    }

    /// <summary>
    /// Drops the two compound indexes the multikey <see cref="MatchDoc.Participants"/> index replaces
    /// — "replace" meaning it, not "grow to three": an index nobody queries through any more is pure
    /// write overhead on every archive save. Mongo's driver has no "create or replace" for indexes with
    /// a different key shape, so this drops the old ones by their default auto-generated names before
    /// <see cref="EnsureIndexesAsync"/> creates the new one; a database that never had them (a fresh
    /// deployment) or one that has already been migrated both report "index not found", which is not
    /// a failure here.
    /// </summary>
    private async Task DropLegacyChallengerOpponentIndexesAsync(CancellationToken ct)
    {
        foreach (var name in new[] { "ChallengerId_1_CreatedAt_-1", "OpponentId_1_CreatedAt_-1" })
        {
            try
            {
                await Matches.Indexes.DropOneAsync(name, ct);
            }
            catch (MongoCommandException ex) when (ex.CodeName == "IndexNotFound")
            {
                // Already gone, or never existed on this database — exactly the steady state this
                // migration converges to.
            }
        }
    }
}
