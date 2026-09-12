using System.Reflection;
using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public class MatchingQuestionIsolationTests
{
    [Fact]
    public void MatchingQuestion_is_sealed_and_shares_no_inheritance_with_Question()
    {
        var matchingType = typeof(MatchingQuestion);
        var questionType = typeof(Question);

        Assert.True(matchingType.IsSealed);
        Assert.False(questionType.IsAssignableFrom(matchingType));
        Assert.False(matchingType.IsAssignableFrom(questionType));
    }

    [Fact]
    public void No_conversion_operator_exists_between_MatchingQuestion_and_Question()
    {
        var matchingType = typeof(MatchingQuestion);
        var questionType = typeof(Question);

        bool ConvertsBetween(Type type) => type
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name is "op_Implicit" or "op_Explicit")
            .Any(m => (m.ReturnType == questionType && m.GetParameters()[0].ParameterType == matchingType)
                   || (m.ReturnType == matchingType && m.GetParameters()[0].ParameterType == questionType));

        Assert.False(ConvertsBetween(matchingType));
        Assert.False(ConvertsBetween(questionType));
    }
}
