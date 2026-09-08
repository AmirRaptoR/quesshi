namespace Quesshi.Shared;

/// <summary>Everything the banner in <c>MainLayout.razor</c> needs to render and act on an invitation.</summary>
public sealed record LiveChallengeDto(string ChallengeId, string ChallengerId, string ChallengerName,
    string ChallengerAvatarSeed, int Lang, int QuestionCount, List<string> CategoryIds, List<int> Levels,
    DateTimeOffset ExpiresAt);
