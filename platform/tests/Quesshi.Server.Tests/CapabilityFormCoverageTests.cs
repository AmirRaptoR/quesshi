using Quesshi.Application;
using Quesshi.Application.Content;
using Quesshi.Application.Identity;
using Quesshi.Features;
using Quesshi.Features.Sources.Bank;
using Quesshi.Features.Sources.Pack;
using Quesshi.Infrastructure.Database;
using Quesshi.Web.Admin;

namespace Quesshi.Server.Tests;

public sealed class CapabilityFormCoverageTests
{
    [Fact]
    public void Every_registered_capability_has_a_named_form_and_settings_bearing_ones_have_their_own()
    {
        var registry = ProductionRegistry();
        var uncovered = registry.Definitions
            .Where(definition => CapabilityForms.For(definition.Type.ToString().ToLowerInvariant(), definition.Key) is null)
            .Select(definition => $"{definition.Type}:{definition.Key}")
            .ToArray();
        var settingsBearingWithoutOwnForm = registry.Definitions
            .Where(definition => definition.SettingsType.GetProperties().Length > 0)
            .Where(definition => CapabilityForms.For(definition.Type.ToString().ToLowerInvariant(), definition.Key) is { Name: "FeatureCapabilityForm" or "FormatCapabilityForm" })
            .Select(definition => $"{definition.Type}:{definition.Key}")
            .ToArray();

        Assert.Equal((Array.Empty<string>(), Array.Empty<string>()), (uncovered, settingsBearingWithoutOwnForm));
    }

    private static CapabilityRegistry ProductionRegistry()
    {
        var features = FeatureRegistry.Create(FeatureRegistry.Discover(typeof(FeaturesMarker).Assembly));
        _ = SignInRegistry.Create(features.Capabilities, SignInRegistry.Discover(typeof(FeaturesMarker).Assembly));
        _ = SourceRegistry.Create(features.Capabilities, [new BankSource(), new PackSource(new PostgresPacks(new TenantLocks()), new TenantLocks())]);
        return features.Capabilities;
    }
}
