namespace Quesshi.Shared;

/// <summary>Everything the banner in <c>MainLayout.razor</c> needs to render and act on an invitation.
/// Carries no settings — the invitation is a pointer at a lobby that already owns them.</summary>
public sealed record LiveChallengeDto(string ChallengeId, string ChallengerId, string ChallengerName,
    string ChallengerAvatarSeed, string LobbyId, string LobbyCode, DateTimeOffset ExpiresAt);
