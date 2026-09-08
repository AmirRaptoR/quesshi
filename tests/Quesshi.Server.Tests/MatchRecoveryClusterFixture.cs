using Orleans.TestingHost;

namespace Quesshi.Server.Tests;

public sealed class MatchRecoveryClusterFixture : IDisposable
{
    public MatchRecoveryClusterFixture()
    {
        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<MatchRecoveryTestSilo>();
        Cluster = builder.Build();
        Cluster.Deploy();
    }

    public TestCluster Cluster { get; }

    public void Dispose() => Cluster.StopAllSilos();
}
