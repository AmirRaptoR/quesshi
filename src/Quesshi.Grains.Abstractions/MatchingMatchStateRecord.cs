namespace Quesshi.Grains.Abstractions;

[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.MatchingMatchStateRecord")]
public sealed class MatchingMatchStateRecord
{
    [Id(0)] public string Json { get; set; } = "";
    [Id(1)] public string Code { get; set; } = "";
    [Id(2)] public string OwnerId { get; set; } = "";
    [Id(3)] public int Lang { get; set; }
    [Id(4)] public int QuestionCount { get; set; }
    [Id(5)] public List<string> CategoryIds { get; set; } = [];
    [Id(6)] public int Capacity { get; set; }
    [Id(7)] public List<string> Participants { get; set; } = [];
    [Id(8)] public DateTimeOffset CreatedAt { get; set; }
    [Id(9)] public int State { get; set; }
    [Id(10)] public DateTimeOffset? EndedAt { get; set; }
    /// <summary>
    /// The hot snapshot was durably written before its archive mirror. A true value is an outbox
    /// marker: activation retries the idempotent archive write before clearing it.
    /// </summary>
    [Id(11)] public bool ArchivePending { get; set; }
    /// <summary>Durable serve token to question id outbox. Tokens are match-id/slot-id pairs, so
    /// replay is idempotent even when multiple matches serve one question concurrently.</summary>
    [Id(12)] public Dictionary<string, string> ServedQuestionPending { get; set; } = [];
}
