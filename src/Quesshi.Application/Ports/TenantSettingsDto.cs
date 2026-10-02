namespace Quesshi.Application.Ports;

/// <summary>Display and feature configuration safe to return to an unauthenticated client.</summary>
public sealed record TenantSettingsDto(
    string Id,
    string Name,
    TenantBrandSettingsDto Brand);

public sealed record TenantBrandSettingsDto(
    string Theme,
    IReadOnlyDictionary<string, string> LandingContent,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> EnabledModes,
    IReadOnlyDictionary<string, string> Rules);
