using MongoDB.Driver;
using Quesshi.Application.Ports;
using Microsoft.Extensions.Logging;

namespace Quesshi.Infrastructure.Mongo;

public sealed class MongoAiSpendLog(MongoContext db, ILogger<MongoAiSpendLog> logger) : IAiSpendLog
{
    public async Task RecordAsync(AiCall call, CancellationToken ct = default)
    {
        try
        {
            await db.AiCalls.InsertOneAsync(AiCallDoc.From(call), cancellationToken: ct);
        }
        catch (Exception ex)
        {
            // Losing a bookkeeping row is a worse outcome than losing the questions it paid for.
            logger.LogWarning(ex, "Could not record AI spend for {Purpose}", call.Purpose);
        }
    }

    public async Task<AiSpend> TotalsAsync(DateTimeOffset? since = null, CancellationToken ct = default)
    {
        var filter = since is null
            ? Builders<AiCallDoc>.Filter.Empty
            : Builders<AiCallDoc>.Filter.Gte(c => c.At, since.Value.UtcDateTime);

        var totals = await db.AiCalls.Aggregate().Match(filter)
            .Group(_ => 1, g => new
            {
                Calls = g.LongCount(),
                Prompt = g.Sum(c => (long)c.PromptTokens),
                Completion = g.Sum(c => (long)c.CompletionTokens),
                Cost = g.Sum(c => c.Cost)
            })
            .FirstOrDefaultAsync(ct);

        return totals is null ? AiSpend.Nothing : new AiSpend(totals.Calls, totals.Prompt, totals.Completion, totals.Cost);
    }
}
