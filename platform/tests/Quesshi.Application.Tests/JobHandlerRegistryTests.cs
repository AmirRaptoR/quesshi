using Quesshi.Application.Jobs;

namespace Quesshi.Application.Tests;

public sealed class JobHandlerRegistryTests
{
    [Fact]
    public void A_handler_is_registered_once_per_owner_and_kind()
    {
        var registry = new JobHandlerRegistry();
        JobHandler handler = (_, _) => Task.CompletedTask;

        registry.Register("plugin", "send", handler, WhenDisabled.Cancel);

        Assert.True(registry.TryGet("plugin", "send", out var registration));
        Assert.Equal(WhenDisabled.Cancel, registration.WhenDisabled);
        Assert.Throws<InvalidOperationException>(() => registry.Register("plugin", "send", handler));
    }
}
