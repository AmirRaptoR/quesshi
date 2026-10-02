namespace Quesshi.Domain;

/// <summary>
/// The immutable content served for one voting slot. Answers belong to the slot internally, but
/// are deliberately not exposed here: before the barrier a caller may read only its own answer via
/// <see cref="VotingMatch.AnswerFor"/>. Raw answers are part of <see cref="VotingMatchSnapshot"/>
/// because persistence and result computation are trusted internal consumers.
/// </summary>
public sealed class VotingSlot
{
    private readonly Dictionary<string, VotingAnswer> _answers = [];

    internal VotingSlot(int slot, string questionId, string prompt,
        IReadOnlyList<VotingServedOption> options, MediaRef media, DateTimeOffset servedAt)
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
    public IReadOnlyList<VotingServedOption> Options { get; }
    public MediaRef Media { get; }
    public DateTimeOffset ServedAt { get; }

    internal IReadOnlyDictionary<string, VotingAnswer> Answers => _answers;

    internal bool HasAnswered(string participantId) => _answers.ContainsKey(participantId);

    internal VotingAnswer? AnswerFor(string participantId) => _answers.GetValueOrDefault(participantId);

    internal void Record(string participantId, VotingAnswer answer) => _answers[participantId] = answer;

    internal VotingSlotSnapshot ToSnapshot() => new(
        Slot, QuestionId, Prompt, [.. Options], ServedAt, new Dictionary<string, VotingAnswer>(_answers), Media);

    internal static VotingSlot Restore(VotingSlotSnapshot snapshot)
    {
        var slot = new VotingSlot(snapshot.Slot, snapshot.QuestionId, snapshot.Prompt,
            snapshot.Options ?? [], snapshot.Media ?? MediaRef.None, snapshot.ServedAt);
        if (snapshot.Answers is not null)
            foreach (var (participantId, answer) in snapshot.Answers)
                slot._answers[participantId] = answer;
        return slot;
    }
}
