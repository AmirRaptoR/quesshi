using Quesshi.Application;

namespace Quesshi.Application.Tests;

public sealed class CapabilityRegistryTests
{
    [Fact]
    public void Registry_registers_typed_definitions_and_rejects_duplicate_keys()
    {
        var registry = new CapabilityRegistry();
        registry.Register(CapabilityType.Feature, "demo", new DemoSettings(true), settings =>
            settings.Label.Length == 0 ? ["A label is required."] : []);

        var definition = Assert.IsType<CapabilityDefinition<DemoSettings>>(registry.Get(CapabilityType.Feature, "demo"));
        Assert.Equal(new DemoSettings(true), definition.DefaultSettings);
        Assert.Equal(["A label is required."], definition.Validate(new DemoSettings(false)));
        Assert.Throws<InvalidOperationException>(() => registry.Register(CapabilityType.Feature, "demo", new DemoSettings(true), _ => []));
    }

    [Theory]
    [InlineData((int)CapabilityType.Kind)]
    [InlineData((int)CapabilityType.Format)]
    [InlineData((int)CapabilityType.Feature)]
    [InlineData((int)CapabilityType.Signin)]
    [InlineData((int)CapabilityType.Source)]
    [InlineData((int)CapabilityType.Switch)]
    public void Registry_accepts_each_capability_type(int typeValue)
    {
        var type = (CapabilityType)typeValue;
        var registry = new CapabilityRegistry();

        registry.Register(type, "plugin-key", new DemoSettings(true), _ => []);

        Assert.IsType<CapabilityDefinition<DemoSettings>>(registry.Get(type, "plugin-key"));
    }

    [Fact]
    public void Core_registry_contains_only_the_documented_core_switches()
    {
        var definitions = CoreCapabilities.CreateRegistry().Definitions;

        Assert.Collection(definitions.OrderBy(definition => definition.Key, StringComparer.Ordinal),
            definition => Assert.Equal((CapabilityType.Switch, "custom_questions"), (definition.Type, definition.Key)),
            definition => Assert.Equal((CapabilityType.Switch, "guests"), (definition.Type, definition.Key)));
    }

    private sealed record DemoSettings(bool Enabled)
    {
        public string Label { get; init; } = string.Empty;
    }
}
