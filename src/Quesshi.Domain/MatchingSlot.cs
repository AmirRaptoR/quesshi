namespace Quesshi.Domain;

/// <summary>
/// The immutable content served for one matching slot. Answers belong to the slot internally, but
/// are deliberately not exposed here: before the barrier a caller may read only its own answer via
/// <see cref="MatchingMatch.AnswerFor"/>. Raw answers are part of <see cref="MatchingMatchSnapshot"/>
/// because persistence and result computation are trusted internal consumers.
/// </summary>
public sealed class MatchingSlot
{
    private readonly Dictionary<string, MatchingAnswer> _answers = [];

    internal MatchingSlot(int slot, string questionId, string prompt,
        IReadOnlyList<MatchingServedOption> options, MediaRef media, DateTimeOffset servedAt)
    {
        Slot = slot;
        QuestionId = questionId;
        Prompt = prompt;
        Options = [.. options];
        Media = media;
        ServedAt = servedAt;
    }

    public int Slot { get; }
    public string QuestionId { get; }
    public string Prompt { get; }
    public IReadOnlyList<MatchingServedOption> Options { get; }
    public MediaRef Media { get; }
    public DateTimeOffset ServedAt { get; }

    internal IReadOnlyDictionary<string, MatchingAnswer> Answers => _answers;

    internal bool HasAnswered(string participantId) => _answers.ContainsKey(participantId);

    internal MatchingAnswer? AnswerFor(string participantId) => _answers.GetValueOrDefault(participantId);

    internal void Record(string participantId, MatchingAnswer answer) => _answers[participantId] = answer;

    internal MatchingSlotSnapshot ToSnapshot() => new(
        Slot, QuestionId, Prompt, [.. Options], ServedAt, new Dictionary<string, MatchingAnswer>(_answers), Media);

    internal static MatchingSlot Restore(MatchingSlotSnapshot snapshot)
    {
        var slot = new MatchingSlot(snapshot.Slot, snapshot.QuestionId, snapshot.Prompt,
            snapshot.Options ?? [], snapshot.Media ?? MediaRef.None, snapshot.ServedAt);
        if (snapshot.Answers is not null)
            foreach (var (participantId, answer) in snapshot.Answers)
                slot._answers[participantId] = answer;
        return slot;
    }
}
