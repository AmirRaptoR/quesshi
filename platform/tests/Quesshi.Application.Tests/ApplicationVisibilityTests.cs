using System.Runtime.CompilerServices;
using System.Reflection;
using Quesshi.Application;
using Quesshi.Application.Sessions;
using Quesshi.Application.Tenancy;

namespace Quesshi.Application.Tests;

public sealed class ApplicationVisibilityTests
{
    [Fact]
    public void Application_contract_exposes_only_the_intended_core_types()
    {
        _ = new ApplicationMarker();
        var exportedTypes = typeof(ApplicationMarker).Assembly.GetExportedTypes()
            .OrderBy(type => type.FullName)
            .ToArray();

        Assert.Equal(
            [
                typeof(AnswerKey),
                typeof(ChangeSet),
                typeof(ISessionDeadlineReconciler),
                typeof(ISessionSnapshotLoader),
                typeof(ISessionTurnObserver),
                typeof(IViewRenderer),
                typeof(NoopSessionDeadlineReconciler),
                typeof(NullSessionTurnObserver),
                typeof(RenderedSessionView),
                typeof(RowChange),
                typeof(SessionFaultResult),
                typeof(SessionItemState),
                typeof(SessionSnapshotLoader),
                typeof(SessionState),
                typeof(SessionStateStore),
                typeof(SessionTurnKind),
                typeof(SessionTurnSample),
                typeof(SessionTurnSource),
                typeof(SessionViewSnapshot),
                typeof(StaleActivationException),
                typeof(ITenants),
                typeof(Tenant),
            ],
            exportedTypes);
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
