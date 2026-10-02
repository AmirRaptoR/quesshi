namespace Quesshi.Shared;

public sealed record VotingOptionDto(
    string Kind,
    string? ParticipantId,
    string? DisplayName,
    int? ChoiceIndex,
    string? Text,
    bool IsNotApplicable,
    string? AvatarSeed = null);
