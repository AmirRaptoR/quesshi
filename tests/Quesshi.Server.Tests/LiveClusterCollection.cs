namespace Quesshi.Server.Tests;

[CollectionDefinition(nameof(LiveClusterCollection))]
public sealed class LiveClusterCollection : ICollectionFixture<LiveClusterFixture>;
