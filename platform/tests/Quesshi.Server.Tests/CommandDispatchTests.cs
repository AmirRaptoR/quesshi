using Quesshi.Server;

namespace Quesshi.Server.Tests;

public sealed class CommandDispatchTests
{
    [Fact]
    public async Task Invalid_tenant_add_arguments_return_a_command_error()
    {
        var exitCode = await Program.Main(["tenant", "add", "--slug", "demo"]);

        Assert.Equal(2, exitCode);
    }
}
