using MongoDB.Bson;
using MongoDB.Driver;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Infrastructure.Mongo;

public sealed class MongoMatchingQuestionRepository(MongoContext db) : IMatchingQuestionRepository
{
    private static readonly FilterDefinitionBuilder<MatchingQuestionDoc> F = Builders<MatchingQuestionDoc>.Filter;

    public async Task<MatchingQuestion?> GetAsync(string id, CancellationToken ct = default)
        => (await db.MatchingQuestions.Find(q => q.Id == id).FirstOrDefaultAsync(ct))?.ToDomain();

    public async Task<IReadOnlyList<MatchingQuestion>> FindAsync(MatchingQuestionFilter filter, CancellationToken ct = default)
    {
        var find = db.MatchingQuestions.Find(Build(filter));
        var sorted = find.SortByDescending(q => q.Source).ThenByDescending(q => q.CreatedAt);
        return [.. (await sorted.Skip(filter.Skip).Limit(filter.Take).ToListAsync(ct)).Select(d => d.ToDomain())];
    }

    public Task<long> CountAsync(MatchingQuestionFilter filter, CancellationToken ct = default)
        => db.MatchingQuestions.CountDocumentsAsync(Build(filter), cancellationToken: ct);

    public async Task<IReadOnlyList<MatchingQuestion>> SampleApprovedAsync(Language lang, string categoryId,
        int count, IReadOnlyCollection<string> exclude, CancellationToken ct = default)
    {
        var filter = F.Eq(q => q.Status, (int)QuestionStatus.Approved)
            & F.Eq(q => q.Lang, (int)lang)
            & F.Eq(q => q.MatchingCategoryId, categoryId)
            & F.Nin(q => q.Id, exclude);
        var docs = await db.MatchingQuestions.Aggregate().Match(filter).Sample(count).ToListAsync(ct);
        return [.. docs.Select(d => d.ToDomain())];
    }

    public Task UpsertAsync(MatchingQuestion question, CancellationToken ct = default)
        => db.MatchingQuestions.UpdateOneAsync(q => q.Id == question.Id, AuthoringUpdate(question),
            new UpdateOptions { IsUpsert = true }, ct);

    public async Task<MatchingServeResult> RecordServedAsync(string id, string serveToken,
        CancellationToken ct = default)
    {
        var filter = F.Eq(q => q.Id, id) & F.Not(F.AnyEq(q => q.ServedTokens, serveToken));
        var update = Builders<MatchingQuestionDoc>.Update
            .AddToSet(q => q.ServedTokens, serveToken)
            .Inc(q => q.TimesServed, 1);
        var result = await db.MatchingQuestions.UpdateOneAsync(filter, update, cancellationToken: ct);
        if (result.ModifiedCount > 0) return MatchingServeResult.Recorded;
        return await db.MatchingQuestions.Find(F.Eq(q => q.Id, id)).AnyAsync(ct)
            ? MatchingServeResult.AlreadyRecorded
            : MatchingServeResult.Missing;
    }

    public async Task<int> UpsertManyAsync(IReadOnlyList<MatchingQuestion> questions, CancellationToken ct = default)
    {
        if (questions.Count == 0) return 0;

        var writes = questions.Select(q =>
            new UpdateOneModel<MatchingQuestionDoc>(F.Eq(d => d.Id, q.Id), AuthoringUpdate(q))
            { IsUpsert = true });

        try
        {
            var result = await db.MatchingQuestions.BulkWriteAsync(writes,
                new BulkWriteOptions { IsOrdered = false }, ct);
            // UpdateOneModel reports a matched-but-unchanged row with MatchedCount rather than
            // ModifiedCount. UpsertMany's contract is accepted rows, not only rows whose bytes
            // changed, so count both existing matches and newly inserted upserts.
            return (int)(result.MatchedCount + result.Upserts.Count);
        }
        catch (MongoBulkWriteException<MatchingQuestionDoc> ex)
        {
            if (ex.WriteErrors.Any(e => e.Category != ServerErrorCategory.DuplicateKey)) throw;
            return questions.Count - ex.WriteErrors.Count;
        }
    }

    public Task DeleteAsync(string id, CancellationToken ct = default)
        => db.MatchingQuestions.DeleteOneAsync(q => q.Id == id, ct);

    public async Task<IReadOnlySet<string>> ExistingTopicsAsync(Language lang, CancellationToken ct = default)
    {
        var filter = F.Eq(q => q.Lang, (int)lang) & F.Type(q => q.Topic, BsonType.String);
        var topics = await db.MatchingQuestions.Distinct(q => q.Topic, filter, cancellationToken: ct).ToListAsync(ct);
        return topics.Where(t => t is not null).Select(t => t!).ToHashSet();
    }

    private static FilterDefinition<MatchingQuestionDoc> Build(MatchingQuestionFilter f)
    {
        var filter = F.Empty;
        if (f.Lang is { } lang) filter &= F.Eq(q => q.Lang, (int)lang);
        if (f.CategoryId is { } category) filter &= F.Eq(q => q.MatchingCategoryId, category);
        if (f.Status is { } status) filter &= F.Eq(q => q.Status, (int)status);
        if (!string.IsNullOrWhiteSpace(f.Text))
            filter &= F.Regex(q => q.Prompt,
                new BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(f.Text), "i"));
        return filter;
    }

    private static UpdateDefinition<MatchingQuestionDoc> AuthoringUpdate(MatchingQuestion q)
    {
        var update = Builders<MatchingQuestionDoc>.Update
            .Set(d => d.Lang, (int)q.Lang)
            .Set(d => d.MatchingCategoryId, q.MatchingCategoryId)
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
        return update
            .SetOnInsert(d => d.Id, q.Id)
            .SetOnInsert(d => d.CreatedAt, q.CreatedAt.UtcDateTime)
            .SetOnInsert(d => d.TimesServed, q.TimesServed)
            .SetOnInsert(d => d.ServedTokens, []);
    }
}
