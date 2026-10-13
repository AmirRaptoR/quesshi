namespace Quesshi.Web.Models;

public sealed record TenantConfig(
    string Name,
    IReadOnlyList<TenantLanguage> Languages,
    string DefaultLanguage,
    IReadOnlyDictionary<string, string[]> Capabilities,
    long ConfigVersion,
    string ServerBuildId);

public sealed record TenantLanguage(string Code, string DisplayName, string Direction, string? AppName);
