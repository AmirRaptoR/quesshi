namespace Quesshi.Domain;

public sealed record PlayerSnapshot(
    string Id, string Email, string DisplayName, string AvatarSeed, Language Lang, bool IsBanned,
    DateTimeOffset CreatedAt, PlayerStats Stats, Dictionary<string, CategoryRecord> ByCategory, List<string> Friends,
    /// <summary>Last and defaulted: player documents written before guests existed simply have none.</summary>
    bool IsGuest = false);
