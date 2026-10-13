using Quesshi.Server;

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
    public async Task Invalid_tenant_add_arguments_return_a_command_error()
    {
        var exitCode = await Program.Main(["tenant", "add", "--slug", "demo"]);

        Assert.Equal(2, exitCode);
    }
}
