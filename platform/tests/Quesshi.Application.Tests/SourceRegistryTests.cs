using System.Text.Json;
using Quesshi.Application.Content;
using Quesshi.Plugin.Api;

namespace Quesshi.Application.Tests;

public sealed class SourceRegistryTests
{
    [Fact]
    public void Registry_resolves_registered_sources_and_rejects_duplicate_ids()
    {
        var source = new TestSource("bank");
        var registry = SourceRegistry.Create(new CapabilityRegistry(), [source]);

        Assert.Same(source, registry.Get("bank"));
        Assert.True(registry.TryGet("bank", out var resolved));
        Assert.Same(source, resolved);
        Assert.Throws<InvalidOperationException>(() => registry.Register(new TestSource("bank")));
    }

    private sealed class TestSource(string id) : ISource
    {
        public string Id { get; } = id;

        public Type Params => typeof(TestParameters);

        public FeatureSettings Settings { get; } = FeatureSettings.Create(new TestSettings(), _ => []);

        public Task<bool> AuthorizeAsync(ITenantTx transaction, JsonElement parameters, Guid actor, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<Picked> PickAsync(ITenantTx transaction, JsonElement parameters, SourceDrawPlan plan, Guid actor, CancellationToken cancellationToken = default) =>
            Task.FromResult<Picked>(new PickedQuestionIds([]));
    }

    private sealed record TestParameters;

    private sealed record TestSettings;
}
