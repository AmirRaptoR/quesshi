namespace Quesshi.Shared;

public sealed record UpdateVotingSettingsDto(
    string? Lang = null,
    int? Questions = null,
    List<string>? Categories = null,
    List<int>? Levels = null,
    int? Capacity = null,
    string? Mode = null);
