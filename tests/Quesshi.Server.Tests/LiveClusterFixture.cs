using Orleans.TestingHost;

namespace Quesshi.Server.Tests;

public sealed class LiveClusterFixture : IDisposable
{
    public LiveClusterFixture()
    {
        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<LiveTestSilo>();
        Cluster = builder.Build();
        Cluster.Deploy();
    }

    public TestCluster Cluster { get; }

    public void Dispose() => Cluster.StopAllSilos();
}
