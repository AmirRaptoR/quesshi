using System.Text.Json;
using Quesshi.Plugin.Api;
using Quesshi.Shared;

namespace Quesshi.Application.Content;

/// <summary>Validates source output at the pre-engine boundary while the tenant transaction is open.</summary>
internal sealed class SourceResultValidator(IContent content, IKindRegistry kinds)
{
    public async Task<IReadOnlyList<QuestionEnvelope>> ValidateAsync(
        ITenantTx transaction,
        Picked picked,
        SourceDrawPlan plan,
        CancellationToken cancellationToken = default)
    {
        if (plan.Count < 1) throw new InvalidSourceResultException("The draw plan count is invalid.");

        return picked switch
        {
            PickedQuestionIds ids => await ValidateIdsAsync(transaction, ids, plan, cancellationToken),
            PickedSnapshots snapshots => await ValidateSnapshotsAsync(transaction, snapshots, plan, cancellationToken),
            _ => throw new InvalidSourceResultException("The source returned an unknown result type."),
        };
    }

    private async Task<IReadOnlyList<QuestionEnvelope>> ValidateIdsAsync(
        ITenantTx transaction,
        PickedQuestionIds picked,
        SourceDrawPlan plan,
        CancellationToken cancellationToken)
    {
        if (picked.Ids.Count != plan.Count || picked.Ids.Distinct().Count() != picked.Ids.Count)
            throw new InvalidSourceResultException("The source returned the wrong number of question ids.");

        var questions = new List<QuestionEnvelope>(picked.Ids.Count);
        foreach (var id in picked.Ids)
        {
            var question = await content.GetAsync(transaction, id, cancellationToken)
                ?? throw new InvalidSourceResultException("The source returned an unavailable question id.");
            await ValidateKindAsync(transaction, question.Kind, question.KindVersion, question.Family, plan, cancellationToken);
            questions.Add(question);
        }

        return questions;
    }

    private async Task<IReadOnlyList<QuestionEnvelope>> ValidateSnapshotsAsync(
        ITenantTx transaction,
        PickedSnapshots picked,
        SourceDrawPlan plan,
        CancellationToken cancellationToken)
    {
        if (picked.Items.Count != plan.Count)
            throw new InvalidSourceResultException("The source returned the wrong number of snapshots.");

        var snapshots = new List<QuestionEnvelope>(picked.Items.Count);
        foreach (var snapshot in picked.Items)
        {
            await ValidateKindAsync(transaction, snapshot.Kind, snapshot.KindVersion, snapshot.Family, plan, cancellationToken);
            if (snapshot.MediaId is not null && !await TenantOwnsMediaAsync(transaction, snapshot.MediaId.Value, cancellationToken))
                throw new InvalidSourceResultException("The snapshot references unavailable media.");

            var kind = kinds.Get(snapshot.Kind, snapshot.KindVersion);
            try
            {
                if (kind.Validate(snapshot.Payload, new ValidateEnv(plan.Language)).Count != 0)
                    throw new InvalidSourceResultException("The snapshot payload is invalid.");
                _ = kind.Card(snapshot.Payload, snapshot.Resolved);
                _ = kind.Reveal(snapshot.Payload, snapshot.Resolved);
                var raw = JsonSerializer.SerializeToElement(new
                {
                    card = snapshot.Card,
                    reveal = snapshot.Reveal,
                    answers = Array.Empty<JsonElement>(),
                });
                _ = kind.UpgradeSnapshot(snapshot.KindVersion, raw);
            }
            catch (JsonException exception)
            {
                throw new InvalidSourceResultException("The snapshot does not match its kind.", exception);
            }

            snapshots.Add(new QuestionEnvelope(
                Guid.Empty, snapshot.Kind, snapshot.KindVersion, snapshot.Family, plan.Language,
                snapshot.CategoryId, snapshot.Level, snapshot.Prompt, snapshot.Payload, null, null,
                string.Empty, QuestionStatus.Approved, QuestionSource.Seed, null, snapshot.MediaId));
        }

        return snapshots;
    }

    private async Task ValidateKindAsync(
        ITenantTx transaction,
        string id,
        int version,
        string family,
        SourceDrawPlan plan,
        CancellationToken cancellationToken)
    {
        var accepted = plan.AcceptedKinds.SingleOrDefault(kind => StringComparer.Ordinal.Equals(kind.Id, id));
        if (accepted is null || !StringComparer.Ordinal.Equals(accepted.Family, family))
            throw new InvalidSourceResultException("The source returned a kind the plan does not accept.");
        if (!kinds.TryGet(id, version, out _))
            throw new InvalidSourceResultException("The source returned an unregistered kind version.");
        if (await transaction.QuerySingleOrDefaultAsync<bool?>("""
                select entitled and enabled
                from tenant_capabilities
                where type = 'kind' and key = @id
                """, new { id }, cancellationToken) != true)
            throw new InvalidSourceResultException("The source returned a disabled kind.");
    }

    private static async Task<bool> TenantOwnsMediaAsync(ITenantTx transaction, Guid mediaId, CancellationToken cancellationToken) =>
        await transaction.QuerySingleOrDefaultAsync<Guid?>("select id from media_assets where id = @mediaId", new { mediaId }, cancellationToken) is not null;
}
