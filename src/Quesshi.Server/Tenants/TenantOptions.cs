namespace Quesshi.Server.Tenants;

public sealed class TenantOptions
{
    public List<TenantDefinition> Tenants { get; set; } = [];
}

public sealed class TenantDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Hosts { get; set; } = [];
    public string Theme { get; set; } = "";
    public Dictionary<string, string> LandingContent { get; set; } = [];
    public List<string> Languages { get; set; } = [];
    public List<string> EnabledModes { get; set; } = [];
    public Dictionary<string, string> Rules { get; set; } = [];
}
