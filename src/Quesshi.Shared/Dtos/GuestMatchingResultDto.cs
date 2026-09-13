namespace Quesshi.Shared;

/// <summary>Guest identity plus the redacted matching lobby snapshot.</summary>
public sealed record GuestMatchingResultDto(string Token, MeDto Me, MatchingViewDto Matching);
