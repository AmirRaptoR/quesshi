using Quesshi.Server.Tenants;

namespace Quesshi.Server.Tests;

public sealed class TenantCommandArgumentsTests
{
    private static TenantRegistry Registry() => new(new TenantOptions
    {
        Tenants =
        [
            new TenantDefinition { Id = "quesshi", Name = "Quesshi", Hosts = ["quesshi.test"] },
            new TenantDefinition { Id = "brand-a", Name = "Brand A", Hosts = ["brand-a.test"] }
        ]
    });

    [Fact]
    public void Maintenance_commands_default_to_the_configured_quesshi_tenant()
    {
        Assert.True(TenantCommandArguments.TryParse([], Registry(), out var parsed, out var error));
        Assert.Null(error);
        Assert.Equal("quesshi", parsed.TenantId);
        Assert.Empty(parsed.Remaining);
    }

    [Fact]
    public void Maintenance_commands_accept_and_validate_an_explicit_tenant()
    {
        Assert.True(TenantCommandArguments.TryParse(["--tenant", "brand-a", "admin", "a@example.test", "password"],
            Registry(), out var parsed, out var error));
        Assert.Null(error);
        Assert.Equal("brand-a", parsed.TenantId);
        Assert.Equal(["admin", "a@example.test", "password"], parsed.Remaining);

        Assert.False(TenantCommandArguments.TryParse(["--tenant", "missing"], Registry(), out _, out error));
        Assert.Contains("not configured", error);
    }
}
