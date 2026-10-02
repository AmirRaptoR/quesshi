namespace Quesshi.Shared;

public sealed record VotingParticipantDto(string Id, string DisplayName, bool Active, bool IsGuest,
    string? AvatarSeed = null);
