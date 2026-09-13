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
}
