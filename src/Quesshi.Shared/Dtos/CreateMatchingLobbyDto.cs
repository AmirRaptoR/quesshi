namespace Quesshi.Shared;

public sealed record CreateMatchingLobbyDto(
    string? Lang = null,
    int? Questions = null,
    List<string>? Categories = null,
    List<int>? Levels = null,
    int Capacity = 2,
    string? Mode = null);
