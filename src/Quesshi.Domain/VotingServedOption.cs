namespace Quesshi.Domain;

/// <summary>
/// An option as it was served by a voting slot. Participant options carry only the stable
/// participant id; fixed options carry their authored text and its position. Special options have
/// neither payload.
/// </summary>
public sealed record VotingServedOption
{
    public VotingAnswerKind Kind { get; }
    public string? ParticipantId { get; }
    public int? ChoiceIndex { get; }
    public string? Text { get; }

    public string? ChoiceText => Text;

    public VotingServedOption(VotingAnswerKind kind, string? participantId = null,
        int? choiceIndex = null, string? text = null)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "The voting option kind is not declared.");

        switch (kind)
        {
            case VotingAnswerKind.SelectedParticipant when participantId is null || choiceIndex is not null || text is not null:
                throw new ArgumentException("A participant option needs only a participant id.");
            case VotingAnswerKind.SelectedChoice when participantId is not null || choiceIndex is null || text is null:
                throw new ArgumentException("A fixed choice option needs its index and text.");
            case VotingAnswerKind.NotApplicable when participantId is not null || choiceIndex is not null || text is not null:
                throw new ArgumentException("A not-applicable option has no payload.");
            case VotingAnswerKind.MultipleParticipants when participantId is not null || choiceIndex is not null || text is not null:
                throw new ArgumentException("A multiple-participants option has no payload.");
            case VotingAnswerKind.NoParticipant when participantId is not null || choiceIndex is not null || text is not null:
                throw new ArgumentException("A no-participant option has no payload.");
        }

        Kind = kind;
        ParticipantId = participantId;
        ChoiceIndex = choiceIndex;
        Text = text;
    }

    public static VotingServedOption ForParticipant(string participantId) =>
        new(VotingAnswerKind.SelectedParticipant, participantId: participantId);

    public static VotingServedOption ForChoice(int choiceIndex, string text) =>
        new(VotingAnswerKind.SelectedChoice, choiceIndex: choiceIndex, text: text);

    public static VotingServedOption NotApplicable() => new(VotingAnswerKind.NotApplicable);

    public static VotingServedOption MultipleParticipants() => new(VotingAnswerKind.MultipleParticipants);

    public static VotingServedOption NoParticipant() => new(VotingAnswerKind.NoParticipant);
}
