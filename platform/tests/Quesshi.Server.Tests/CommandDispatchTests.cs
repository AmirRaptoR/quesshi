using Quesshi.Server;
using Quesshi.Server.Commands;

namespace Quesshi.Server.Tests;

public sealed class CommandDispatchTests
{
    [Theory]
    [InlineData()]
    [InlineData("serve")]
    public async Task Default_and_explicit_serve_dispatch_to_the_host(params string[] args)
    {
        var served = false;
        var exitCode = await Program.DispatchAsync(args, () => { served = true; return Task.CompletedTask; });

        Assert.Equal(0, exitCode);
        Assert.True(served);
    }

    [Fact]
    public async Task Existing_commands_remain_dispatchable_from_the_server_entry_point()
    {
        var commands = new List<string>();

        Assert.Equal(0, await Program.DispatchAsync(["migrate"], migrate: () => { commands.Add("migrate"); return Task.CompletedTask; }));
        Assert.Equal(0, await Program.DispatchAsync(["sync"], sync: () => { commands.Add("sync"); return Task.FromResult(0); }));
        Assert.Equal(0, await Program.DispatchAsync(["tenant", "add", "--slug", "demo"], tenantAdd: values => { commands.Add($"tenant {values[0]}"); return Task.CompletedTask; }));

        Assert.Equal(["migrate", "sync", "tenant --slug"], commands);
    }

    [Fact]
    public async Task Admin_add_dispatches_its_arguments_to_the_command()
    {
        IReadOnlyList<string>? received = null;

        var exitCode = await Program.DispatchAsync(
            ["admin", "add", "--tenant", "demo", "--platform", "owner", "owner@example.test", "Strong password 2026!"],
            adminAdd: options => { received = [options.TenantSlug!, options.Username]; return Task.CompletedTask; });

        Assert.Equal(0, exitCode);
        Assert.Equal("demo", received![0]);
    }

    [Theory]
    [InlineData("--tenant")]
    [InlineData("--platform", "--platform", "owner", "owner@example.test", "Strong password 2026!")]
    [InlineData("owner", "owner@example.test")]
    public void Admin_add_rejects_missing_or_conflicting_arguments_without_echoing_a_password(params string[] args)
    {
        var exception = Assert.Throws<CommandException>(() => AdminAddOptions.Parse(args));

        Assert.DoesNotContain("Strong password 2026!", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Admin_add_accepts_tenant_and_platform_flags_together()
    {
        var options = AdminAddOptions.Parse(["--tenant", "demo", "--platform", "owner", "owner@example.test", "Strong password 2026!"]);

        Assert.True(options.IsPlatform && options.TenantSlug == "demo" && options.Username == "owner");
    }

    [Fact]
    public async Task Invalid_admin_add_arguments_return_a_command_error()
    {
        var exitCode = await Program.DispatchAsync(["admin", "add", "--tenant"]);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Invalid_tenant_add_arguments_return_a_command_error()
    {
        var exitCode = await Program.Main(["tenant", "add", "--slug", "demo"]);

        Assert.Equal(2, exitCode);
    }
}
