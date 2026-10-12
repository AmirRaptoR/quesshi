using System.Runtime.CompilerServices;
using System.Reflection;
using Quesshi.Application;

namespace Quesshi.Application.Tests;

public sealed class ApplicationVisibilityTests
{
    [Fact]
    public void Scaffold_exposes_no_public_core_types()
    {
        _ = new ApplicationMarker();
        Assert.Empty(typeof(ApplicationMarker).Assembly.GetExportedTypes());
    }

    [Fact]
    public void Internals_are_visible_only_to_platform_implementation_and_test_assemblies()
    {
        var friends = typeof(ApplicationMarker).Assembly
            .GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(attribute => attribute.AssemblyName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("Quesshi.Infrastructure", friends);
        Assert.Contains("Quesshi.Grains", friends);
        Assert.Contains("Quesshi.Server", friends);
        Assert.Contains("Quesshi.Architecture.Tests", friends);
        Assert.DoesNotContain("Quesshi.Features", friends);
        Assert.DoesNotContain("Quesshi.Kinds", friends);
        Assert.DoesNotContain("Quesshi.Formats", friends);
    }
}
