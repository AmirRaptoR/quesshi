using MongoDB.Bson;
using MongoDB.Driver;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Infrastructure.Mongo;

public sealed class MongoVotingQuestionRepository(MongoContext db) : IVotingQuestionRepository
{
    private static readonly FilterDefinitionBuilder<QuestionDoc> F = Builders<QuestionDoc>.Filter;
    private static FilterDefinition<QuestionDoc> Voting => F.Eq(q => q.Family, (int)QuestionFamily.Voting);

    public async Task<VotingQuestion?> GetAsync(string id, CancellationToken ct = default)
        => (await db.Questions.Find(F.Eq(q => q.Id, id) & Voting).FirstOrDefaultAsync(ct))?.ToVoting();

    public async Task<IReadOnlyList<VotingQuestion>> FindAsync(VotingQuestionFilter filter, CancellationToken ct = default)
    {
        var find = db.Questions.Find(Build(filter));
        var sorted = find.SortByDescending(q => q.Source).ThenByDescending(q => q.CreatedAt);
        return [.. (await sorted.Skip(filter.Skip).Limit(filter.Take).ToListAsync(ct)).Select(d => d.ToVoting())];
    }

    public Task<long> CountAsync(VotingQuestionFilter filter, CancellationToken ct = default)
        => db.Questions.CountDocumentsAsync(Build(filter), cancellationToken: ct);

    public async Task<IReadOnlyList<VotingQuestion>> SampleApprovedAsync(Language lang, string? categoryId,
        int count, IReadOnlyCollection<string> exclude, CancellationToken ct = default)
    {
        var filter = Voting & F.Eq(q => q.OwnerId, null)
            & F.Eq(q => q.Status, (int)QuestionStatus.Approved)
            & F.Eq(q => q.Lang, (int)lang) & F.Nin(q => q.Id, exclude);
        if (categoryId is not null) filter &= F.Eq(q => q.CategoryId, categoryId);
        var docs = await db.Questions.Aggregate().Match(filter).Sample(count).ToListAsync(ct);
        return [.. docs.Select(d => d.ToVoting())];
    }

    public async Task<IReadOnlyList<VotingQuestion>> SampleApprovedAsync(Language lang, ContentScope scope,
        int count, IReadOnlyCollection<string> exclude, CancellationToken ct = default)
    {
        if (scope.CategoryIds is { Count: 0 }) return [];
        var filter = Voting & F.Eq(q => q.Status, (int)QuestionStatus.Approved)
            & F.Eq(q => q.Lang, (int)lang) & F.Nin(q => q.Id, exclude);
        if (scope.CategoryIds is { } categories) filter &= F.In(q => q.CategoryId, categories);
        filter &= scope.OwnerId is null ? F.Eq(q => q.OwnerId, null) : F.Eq(q => q.OwnerId, scope.OwnerId);
        var docs = await db.Questions.Aggregate().Match(filter).Sample(count).ToListAsync(ct);
        return [.. docs.Select(d => d.ToVoting())];
    }

    public Task UpsertAsync(VotingQuestion question, CancellationToken ct = default)
        => db.Questions.UpdateOneAsync(F.Eq(q => q.Id, question.Id) & Voting, AuthoringUpdate(question),
            new UpdateOptions { IsUpsert = true }, ct);

    public async Task<VotingServeResult> RecordServedAsync(string id, string serveToken,
        CancellationToken ct = default)
    {
        var filter = Voting & F.Eq(q => q.Id, id) & F.Not(F.AnyEq(q => q.ServedTokens, serveToken));
        var update = Builders<QuestionDoc>.Update.AddToSet(q => q.ServedTokens, serveToken).Inc(q => q.TimesServed, 1);
        var result = await db.Questions.UpdateOneAsync(filter, update, cancellationToken: ct);
        if (result.ModifiedCount > 0) return VotingServeResult.Recorded;
        return await db.Questions.Find(Voting & F.Eq(q => q.Id, id)).AnyAsync(ct)
            ? VotingServeResult.AlreadyRecorded
            : VotingServeResult.Missing;
    }

    public async Task<int> UpsertManyAsync(IReadOnlyList<VotingQuestion> questions, CancellationToken ct = default)
    {
        if (questions.Count == 0) return 0;
        var writes = questions.Select(q => new UpdateOneModel<QuestionDoc>(F.Eq(d => d.Id, q.Id) & Voting,
            AuthoringUpdate(q))
        { IsUpsert = true });
        try
        {
            var result = await db.Questions.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, ct);
            return (int)(result.MatchedCount + result.Upserts.Count);
        }
        catch (MongoBulkWriteException<QuestionDoc> ex)
        {
            if (ex.WriteErrors.Any(e => e.Category != ServerErrorCategory.DuplicateKey)) throw;
            return questions.Count - ex.WriteErrors.Count;
        }
    }

    public Task DeleteAsync(string id, CancellationToken ct = default)
        => db.Questions.DeleteOneAsync(Voting & F.Eq(q => q.Id, id), ct);

    public async Task<IReadOnlySet<string>> ExistingTopicsAsync(Language lang, CancellationToken ct = default)
    {
        var filter = Voting & F.Eq(q => q.Lang, (int)lang) & F.Type(q => q.Topic, BsonType.String);
        var topics = await db.Questions.Distinct(q => q.Topic, filter, cancellationToken: ct).ToListAsync(ct);
        return topics.Where(t => t is not null).Select(t => t!).ToHashSet();
    }

    public async Task<IReadOnlyCollection<string>> ExistingPromptsAsync(Language lang, string categoryId,
        CancellationToken ct = default)
        => await db.Questions.Find(Voting & F.Eq(q => q.Lang, (int)lang) & F.Eq(q => q.CategoryId, categoryId))
            .Project(q => q.Prompt).ToListAsync(ct);

    private static FilterDefinition<QuestionDoc> Build(VotingQuestionFilter f)
    {
        var filter = Voting;
        if (f.Lang is { } lang) filter &= F.Eq(q => q.Lang, (int)lang);
        if (f.CategoryId is { } category) filter &= F.Eq(q => q.CategoryId, category);
        if (f.Status is { } status) filter &= F.Eq(q => q.Status, (int)status);
        if (!string.IsNullOrWhiteSpace(f.Text))
            filter &= F.Regex(q => q.Prompt,
                new BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(f.Text), "i"));
        return filter;
    }

    private static UpdateDefinition<QuestionDoc> AuthoringUpdate(VotingQuestion q)
    {
        var update = Builders<QuestionDoc>.Update
            .Set(d => d.Family, (int)QuestionFamily.Voting)
            .Set(d => d.OwnerId, q.OwnerId)
            .Set(d => d.Lang, (int)q.Lang)
            .Set(d => d.CategoryId, q.CategoryId)
            .Set(d => d.Prompt, q.Prompt)
            .Set(d => d.AnswerSource, (int)q.AnswerSource)
            .Set(d => d.FixedChoices, [.. q.FixedChoices])
            .Set(d => d.MediaKind, (int)q.Media.Kind)
            .Set(d => d.MediaUrl, q.Media.Url)
            .Set(d => d.MediaAttribution, q.Media.Attribution)
            .Set(d => d.Topic, q.Topic)
            .Set(d => d.Status, (int)q.Status)
            .Set(d => d.Source, (int)q.Source)
            .Set(d => d.UpdatedAt, q.UpdatedAt.UtcDateTime);
        return update.SetOnInsert(d => d.Id, q.Id)
            .SetOnInsert(d => d.CreatedAt, q.CreatedAt.UtcDateTime)
            .SetOnInsert(d => d.TimesServed, q.TimesServed)
            .SetOnInsert(d => d.ServedTokens, []);
    }
}
