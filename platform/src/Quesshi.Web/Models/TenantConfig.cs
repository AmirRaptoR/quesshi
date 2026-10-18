namespace Quesshi.Web.Models;

public sealed record TenantConfig(
    string Name,
    IReadOnlyList<TenantLanguage> Languages,
    string DefaultLanguage,
    IReadOnlyDictionary<string, string[]> Capabilities,
    long ConfigVersion,
    string ServerBuildId)
{
    public IReadOnlyList<SignInMethod> SignInMethods { get; init; } = [];
}

public sealed record TenantLanguage(string Code, string DisplayName, string Direction, string? AppName);

public sealed record SignInMethod(string Id, SignInMethodSettings? Settings);

public sealed record SignInMethodSettings(string? ClientId);
