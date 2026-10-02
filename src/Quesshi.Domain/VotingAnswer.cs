namespace Quesshi.Domain;

/// <summary>
/// One submission for one voting slot. Absence from a slot's answer map means that no answer has
/// been submitted; it is intentionally not represented by a sentinel answer or choice index.
/// </summary>
public sealed record VotingAnswer
{
    public VotingAnswerKind Kind { get; }
    public string? ParticipantId { get; }
    public int? ChoiceIndex { get; }
    public DateTimeOffset At { get; }

    public VotingAnswer(VotingAnswerKind kind, string? participantId, int? choiceIndex, DateTimeOffset at)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "The voting answer kind is not declared.");

        switch (kind)
        {
            case VotingAnswerKind.SelectedParticipant when participantId is null || choiceIndex is not null:
                throw new ArgumentException("A participant answer needs a participant id and no choice index.");
            case VotingAnswerKind.SelectedChoice when participantId is not null || choiceIndex is null:
                throw new ArgumentException("A choice answer needs a choice index and no participant id.");
            case VotingAnswerKind.NotApplicable when participantId is not null || choiceIndex is not null:
                throw new ArgumentException("A not-applicable answer has neither a participant nor a choice index.");
            case VotingAnswerKind.MultipleParticipants when participantId is not null || choiceIndex is not null:
                throw new ArgumentException("A multiple-participants answer has neither a participant nor a choice index.");
            case VotingAnswerKind.NoParticipant when participantId is not null || choiceIndex is not null:
                throw new ArgumentException("A no-participant answer has neither a participant nor a choice index.");
        }

        Kind = kind;
        ParticipantId = participantId;
        ChoiceIndex = choiceIndex;
        At = at;
    }

    public static VotingAnswer SelectedParticipant(string participantId, DateTimeOffset at) =>
        new(VotingAnswerKind.SelectedParticipant, participantId, null, at);

    public static VotingAnswer SelectedChoice(int choiceIndex, DateTimeOffset at) =>
        new(VotingAnswerKind.SelectedChoice, null, choiceIndex, at);

    public static VotingAnswer NotApplicable(DateTimeOffset at) =>
        new(VotingAnswerKind.NotApplicable, null, null, at);

    public static VotingAnswer MultipleParticipants(DateTimeOffset at) =>
        new(VotingAnswerKind.MultipleParticipants, null, null, at);

    public static VotingAnswer NoParticipant(DateTimeOffset at) =>
        new(VotingAnswerKind.NoParticipant, null, null, at);
}
