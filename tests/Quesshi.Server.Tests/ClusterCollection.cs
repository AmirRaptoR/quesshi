using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Quesshi.Application.Ports;
using Quesshi.Domain;

namespace Quesshi.Server.Tests;

[CollectionDefinition(nameof(ClusterCollection))]
public sealed class ClusterCollection : ICollectionFixture<ClusterFixture>;
