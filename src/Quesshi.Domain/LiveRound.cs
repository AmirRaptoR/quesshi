namespace Quesshi.Domain;

/// <summary>
/// One round of a live duel: both players face <see cref="QuestionId"/> at the same time, from the
/// same <see cref="StartedAt"/>. This is the whole difference from <see cref="PlayerRun"/> — answers
/// belong to a round, not to a player, because a round only closes once everyone in it has answered.
/// </summary>
public sealed class LiveRound
{
    private readonly Dictionary<string, LiveAnswer> _answers = [];

    public LiveRound(int slot, string questionId, DateTimeOffset startedAt)
    {
        Slot = slot;
        QuestionId = questionId;
        StartedAt = startedAt;
    }

    public int Slot { get; }
    public string QuestionId { get; }
    public DateTimeOffset StartedAt { get; }
    public IReadOnlyDictionary<string, LiveAnswer> Answers => _answers;

    public bool HasAnswered(string playerId) => _answers.ContainsKey(playerId);

    internal void Record(string playerId, LiveAnswer answer) => _answers[playerId] = answer;

    internal static LiveRound Restore(int slot, string questionId, DateTimeOffset startedAt,
        IReadOnlyDictionary<string, LiveAnswer> answers)
    {
        var round = new LiveRound(slot, questionId, startedAt);
        foreach (var (playerId, answer) in answers) round._answers[playerId] = answer;
        return round;
    }
}
