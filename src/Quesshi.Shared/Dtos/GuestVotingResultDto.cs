namespace Quesshi.Shared;

/// <summary>Guest identity plus the redacted voting lobby snapshot.</summary>
public sealed record GuestVotingResultDto(string Token, MeDto Me, VotingViewDto Voting);
