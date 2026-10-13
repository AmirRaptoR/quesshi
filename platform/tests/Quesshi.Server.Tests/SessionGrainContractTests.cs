using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Quesshi.Grains.Abstractions;

namespace Quesshi.Server.Tests;

public sealed class SessionGrainContractTests
{
    [Fact]
    public void Session_command_and_result_round_trip_through_the_orleans_serializer()
    {
        var services = new ServiceCollection().AddSerializer();
        using var provider = services.BuildServiceProvider();
        var serializer = provider.GetRequiredService<Serializer>();
        var command = new SessionCommand("player", "answer", "2", 4, 1, 3, "choice", "{\"id\":\"a\"}", DateTimeOffset.UnixEpoch);
        var result = new SessionCommandResult("ok", false, 9, "{\"view\":true}");

        Assert.Equal(command, serializer.Deserialize<SessionCommand>(serializer.SerializeToArray(command)));
        Assert.Equal(result, serializer.Deserialize<SessionCommandResult>(serializer.SerializeToArray(result)));
    }
}
