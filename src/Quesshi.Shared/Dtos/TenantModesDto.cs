namespace Quesshi.Shared;

/// <summary>The game modes available on the current tenant host.</summary>
public sealed record TenantModesDto(IReadOnlyList<string> EnabledModes);
