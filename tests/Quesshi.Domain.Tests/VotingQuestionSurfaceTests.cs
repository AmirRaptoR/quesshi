using System.Reflection;
using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public class VotingQuestionSurfaceTests
{
    private static readonly string[] ForbiddenNames =
    [
        "CorrectIndex", "Level", "Target", "BaseLayer", "Explanation", "TimesCorrect", "IsCorrect",
        "Report", "ReportCount", "Reports", "DismissReports"
    ];
    private static readonly Type[] ForbiddenTypes =
        [typeof(Difficulty), typeof(MapTarget), typeof(MapBaseLayer), typeof(QuestionKind)];

    [Fact]
    public void Public_surface_has_no_trivia_only_members()
    {
        var properties = typeof(VotingQuestion).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        Assert.All(properties, p => Assert.DoesNotContain(p.Name, ForbiddenNames));
    }

    [Fact]
    public void Public_surface_has_no_trivia_types_in_properties_fields_or_methods()
    {
        var type = typeof(VotingQuestion);
        var members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);

        var exposedTypes = members.SelectMany(member => member switch
        {
            PropertyInfo property => [property.PropertyType],
            FieldInfo field => [field.FieldType],
            MethodInfo method => new[] { method.ReturnType }.Concat(method.GetParameters().Select(p => p.ParameterType)),
            _ => []
        });

        var forbidden = ForbiddenTypes.ToHashSet();
        foreach (var exposedType in exposedTypes)
        {
            Assert.DoesNotContain(exposedType, forbidden);
            Assert.False(exposedType.IsGenericType && exposedType.GetGenericArguments().Any(forbidden.Contains));
        }
    }
}
