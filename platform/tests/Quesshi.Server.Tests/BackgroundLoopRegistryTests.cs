using Quesshi.Server.Health;

namespace Quesshi.Server.Tests;

public sealed class BackgroundLoopRegistryTests
{
    [Fact]
    public void Registered_loop_is_unhealthy_until_its_first_pass()
    {
        var registry = new BackgroundLoopRegistry();
        registry.Register("job-worker");

        var loop = Assert.Single(registry.Snapshot());
        Assert.Null(loop.LastSuccessfulPassAge);
        Assert.False(loop.IsHealthy);
    }

    [Fact]
    public void Passed_loop_is_healthy()
    {
        var registry = new BackgroundLoopRegistry();
        var loop = registry.Register("job-worker");
        loop.Passed();

        Assert.True(Assert.Single(registry.Snapshot()).IsHealthy);
    }
}
