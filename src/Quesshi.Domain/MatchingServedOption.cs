namespace Quesshi.Domain;

/// <summary>
/// An option as it was served by a matching slot. Participant options carry only the stable
/// participant id; fixed options carry their authored text and its position. Special options have
/// neither payload.
/// </summary>
public sealed record MatchingServedOption
{
    public MatchingAnswerKind Kind { get; }
    public string? ParticipantId { get; }
    public int? ChoiceIndex { get; }
    public string? Text { get; }

    public string? ChoiceText => Text;

    public MatchingServedOption(MatchingAnswerKind kind, string? participantId = null,
        int? choiceIndex = null, string? text = null)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "The matching option kind is not declared.");

        switch (kind)
        {
            case MatchingAnswerKind.SelectedParticipant when participantId is null || choiceIndex is not null || text is not null:
                throw new ArgumentException("A participant option needs only a participant id.");
            case MatchingAnswerKind.SelectedChoice when participantId is not null || choiceIndex is null || text is null:
                throw new ArgumentException("A fixed choice option needs its index and text.");
            case MatchingAnswerKind.NotApplicable when participantId is not null || choiceIndex is not null || text is not null:
                throw new ArgumentException("A not-applicable option has no payload.");
            case MatchingAnswerKind.MultipleParticipants when participantId is not null || choiceIndex is not null || text is not null:
                throw new ArgumentException("A multiple-participants option has no payload.");
            case MatchingAnswerKind.NoParticipant when participantId is not null || choiceIndex is not null || text is not null:
                throw new ArgumentException("A no-participant option has no payload.");
        }

        Kind = kind;
        ParticipantId = participantId;
        ChoiceIndex = choiceIndex;
        Text = text;
    }

    public static MatchingServedOption ForParticipant(string participantId) =>
        new(MatchingAnswerKind.SelectedParticipant, participantId: participantId);

    public static MatchingServedOption ForChoice(int choiceIndex, string text) =>
        new(MatchingAnswerKind.SelectedChoice, choiceIndex: choiceIndex, text: text);

    public static MatchingServedOption NotApplicable() => new(MatchingAnswerKind.NotApplicable);

    public static MatchingServedOption MultipleParticipants() => new(MatchingAnswerKind.MultipleParticipants);

    public static MatchingServedOption NoParticipant() => new(MatchingAnswerKind.NoParticipant);
}
