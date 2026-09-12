using System.Reflection;
using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public class MatchingQuestionSurfaceTests
{
    private static readonly string[] ForbiddenNames = ["CorrectIndex", "Level", "Target", "BaseLayer", "Explanation"];
    private static readonly Type[] ForbiddenTypes = [typeof(Difficulty), typeof(MapTarget), typeof(MapBaseLayer)];

    [Fact]
    public void Public_surface_has_no_trivia_only_members()
    {
        var properties = typeof(MatchingQuestion).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        Assert.All(properties, p => Assert.DoesNotContain(p.Name, ForbiddenNames));
        Assert.All(properties, p => Assert.DoesNotContain(Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType, ForbiddenTypes));
    }
}
