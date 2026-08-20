namespace Quesshi.Shared;

/// <summary>What someone who followed an invite link types instead of signing in: a name.</summary>
public sealed record GuestJoinDto(string Name, string? Lang = null);
