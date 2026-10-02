namespace Quesshi.Server.Tenants;

/// <summary>Parses the optional tenant selector shared by database maintenance commands.</summary>
public sealed record TenantCommandArguments(string TenantId, string[] Remaining)
{
    public static bool TryParse(string[] arguments, TenantRegistry registry,
        out TenantCommandArguments parsed, out string? error)
    {
        var tenantId = "quesshi";
        var remaining = arguments;
        error = null;

        if (arguments.Length > 0 && arguments[0] == "--tenant")
        {
            if (arguments.Length < 2 || string.IsNullOrWhiteSpace(arguments[1]))
            {
                parsed = new TenantCommandArguments(tenantId, []);
                error = "--tenant requires a configured tenant ID.";
                return false;
            }

            tenantId = arguments[1];
            remaining = arguments[2..];
        }

        if (!registry.ContainsTenant(tenantId))
        {
            parsed = new TenantCommandArguments(tenantId, remaining);
            error = $"Tenant '{tenantId}' is not configured.";
            return false;
        }

        parsed = new TenantCommandArguments(tenantId, remaining);
        return true;
    }
}
