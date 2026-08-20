namespace Quesshi.Shared;

public sealed record MeDto(string Id, string DisplayName, string Email, string AvatarSeed, string Lang,
    StatsDto Stats, Dictionary<string, double> Accuracy, List<FriendDto> Friends,
    /// <summary>A guest sees one duel and its result, and nothing else in the app.</summary>
    bool IsGuest = false);
