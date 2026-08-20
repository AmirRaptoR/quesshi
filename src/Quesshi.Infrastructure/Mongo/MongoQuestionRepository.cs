using MongoDB.Bson;
using MongoDB.Driver;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Infrastructure.Mongo;

public sealed class MongoQuestionRepository(MongoContext db) : IQuestionRepository
{
    private static readonly FilterDefinitionBuilder<QuestionDoc> F = Builders<QuestionDoc>.Filter;

    public async Task<Question?> GetAsync(string id, CancellationToken ct = default)
        => (await db.Questions.Find(q => q.Id == id).FirstOrDefaultAsync(ct))?.ToDomain();

    public async Task<IReadOnlyList<Question>> GetManyAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        var docs = await db.Questions.Find(F.In(q => q.Id, ids)).ToListAsync(ct);

        // Preserve the caller's order: a match's question order is part of its fairness contract.
        var byId = docs.ToDictionary(d => d.Id);
        return [.. ids.Select(byId.GetValueOrDefault).Where(d => d is not null).Select(d => d!.ToDomain())];
    }

    public async Task<IReadOnlyList<Question>> FindAsync(QuestionFilter filter, CancellationToken ct = default)
    {
        var find = db.Questions.Find(Build(filter));

        // When looking at complaints, the worst offenders belong at the top. Otherwise the machine's
        // output leads: QuestionSource runs Seed(0) < Ai(1) < Admin(2), so descending floats
        // everything added after the seed bank — the rows an admin actually has to look at.
        var sorted = filter.Reported == true
            ? find.SortByDescending(q => q.ReportCount).ThenByDescending(q => q.CreatedAt)
            : find.SortByDescending(q => q.Source).ThenByDescending(q => q.CreatedAt);

        return [.. (await sorted.Skip(filter.Skip).Limit(filter.Take).ToListAsync(ct)).Select(d => d.ToDomain())];
    }

    public Task<long> CountAsync(QuestionFilter filter, CancellationToken ct = default)
        => db.Questions.CountDocumentsAsync(Build(filter), cancellationToken: ct);

    public async Task<IReadOnlyList<Question>> SampleApprovedAsync(Language lang, string categoryId, Difficulty level,
        int count, IReadOnlyCollection<string> exclude, CancellationToken ct = default)
    {
        var filter = F.Eq(q => q.Status, (int)QuestionStatus.Approved)
                   & F.Eq(q => q.Lang, (int)lang)
                   & F.Eq(q => q.CategoryId, categoryId)
                   & F.Eq(q => q.Level, (int)level)
                   & F.Nin(q => q.Id, exclude);

        var docs = await db.Questions.Aggregate().Match(filter).Sample(count).ToListAsync(ct);
        return [.. docs.Select(d => d.ToDomain())];
    }

    public async Task<IReadOnlyList<BucketCount>> BucketCountsAsync(CancellationToken ct = default)
    {
        var docs = await db.Questions.Find(F.Empty)
            .Project(q => new { q.Lang, q.CategoryId, q.Level, q.Status })
            .ToListAsync(ct);

        return [.. docs.GroupBy(d => (d.Lang, d.CategoryId, d.Level))
            .Select(g => new BucketCount((Language)g.Key.Lang, g.Key.CategoryId, (Difficulty)g.Key.Level,
                g.Count(x => x.Status == (int)QuestionStatus.Approved),
                g.Count(x => x.Status == (int)QuestionStatus.Pending)))];
    }

    public Task UpsertAsync(Question question, CancellationToken ct = default)
        => db.Questions.ReplaceOneAsync(q => q.Id == question.Id, QuestionDoc.From(question),
            new ReplaceOptions { IsUpsert = true }, ct);

    public async Task<int> UpsertManyAsync(IReadOnlyList<Question> questions, CancellationToken ct = default)
    {
        if (questions.Count == 0) return 0;

        var writes = questions.Select(q =>
            new ReplaceOneModel<QuestionDoc>(F.Eq(d => d.Id, q.Id), QuestionDoc.From(q)) { IsUpsert = true });

        try
        {
            // Unordered, so one question colliding on (language, topic) does not stop the rest.
            var result = await db.Questions.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, ct);
            return (int)(result.Upserts.Count + result.ModifiedCount);
        }
        catch (MongoBulkWriteException<QuestionDoc> ex)
        {
            // A duplicate topic is the index doing its job — the question already exists in this
            // language. Anything else is a real failure and still belongs upstairs.
            if (ex.WriteErrors.Any(e => e.Category != ServerErrorCategory.DuplicateKey)) throw;

            return questions.Count - ex.WriteErrors.Count;
        }
    }

    public Task DeleteAsync(string id, CancellationToken ct = default)
        => db.Questions.DeleteOneAsync(q => q.Id == id, ct);

    public async Task<IReadOnlyCollection<(string Prompt, string Answer)>> ExistingQuestionsAsync(string categoryId, CancellationToken ct = default)
    {
        var docs = await db.Questions.Find(q => q.CategoryId == categoryId)
            .Project(q => new { q.Prompt, q.Choices, q.CorrectIndex }).ToListAsync(ct);

        return [.. docs.Select(d => (d.Prompt,
            d.CorrectIndex >= 0 && d.CorrectIndex < d.Choices.Count ? d.Choices[d.CorrectIndex] : ""))];
    }

    private static FilterDefinition<QuestionDoc> Build(QuestionFilter f)
    {
        var filter = F.Empty;
        if (f.Lang is { } lang) filter &= F.Eq(q => q.Lang, (int)lang);
        if (f.CategoryId is { } cat) filter &= F.Eq(q => q.CategoryId, cat);
        if (f.Level is { } level) filter &= F.Eq(q => q.Level, (int)level);
        if (f.Status is { } status) filter &= F.Eq(q => q.Status, (int)status);
        if (f.Reported is { } reported)
            filter &= reported ? F.Gt(q => q.ReportCount, 0) : F.Eq(q => q.ReportCount, 0);
        if (!string.IsNullOrWhiteSpace(f.Text))
            filter &= F.Regex(q => q.Prompt, new BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(f.Text), "i"));
        return filter;
    }
}
