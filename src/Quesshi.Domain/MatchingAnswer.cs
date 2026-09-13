namespace Quesshi.Domain;

/// <summary>
/// One submission for one matching slot. Absence from a slot's answer map means that no answer has
/// been submitted; it is intentionally not represented by a sentinel answer or choice index.
/// </summary>
public sealed record MatchingAnswer
{
    public MatchingAnswerKind Kind { get; }
    public string? ParticipantId { get; }
    public int? ChoiceIndex { get; }
    public DateTimeOffset At { get; }

    public MatchingAnswer(MatchingAnswerKind kind, string? participantId, int? choiceIndex, DateTimeOffset at)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "The matching answer kind is not declared.");

        switch (kind)
        {
            case MatchingAnswerKind.SelectedParticipant when participantId is null || choiceIndex is not null:
                throw new ArgumentException("A participant answer needs a participant id and no choice index.");
            case MatchingAnswerKind.SelectedChoice when participantId is not null || choiceIndex is null:
                throw new ArgumentException("A choice answer needs a choice index and no participant id.");
            case MatchingAnswerKind.NotApplicable when participantId is not null || choiceIndex is not null:
                throw new ArgumentException("A not-applicable answer has neither a participant nor a choice index.");
        }

        Kind = kind;
        ParticipantId = participantId;
        ChoiceIndex = choiceIndex;
        At = at;
    }

    public static MatchingAnswer SelectedParticipant(string participantId, DateTimeOffset at) =>
        new(MatchingAnswerKind.SelectedParticipant, participantId, null, at);

    public static MatchingAnswer SelectedChoice(int choiceIndex, DateTimeOffset at) =>
        new(MatchingAnswerKind.SelectedChoice, null, choiceIndex, at);

    public static MatchingAnswer NotApplicable(DateTimeOffset at) =>
        new(MatchingAnswerKind.NotApplicable, null, null, at);
}
