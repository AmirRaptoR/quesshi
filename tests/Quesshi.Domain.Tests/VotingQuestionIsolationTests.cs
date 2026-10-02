using System.Reflection;
using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public class VotingQuestionIsolationTests
{
    [Fact]
    public void VotingQuestion_is_sealed_and_shares_no_inheritance_with_Question()
    {
        var votingType = typeof(VotingQuestion);
        var questionType = typeof(Question);

        Assert.True(votingType.IsSealed);
        Assert.False(questionType.IsAssignableFrom(votingType));
        Assert.False(votingType.IsAssignableFrom(questionType));
    }

    [Fact]
    public void No_conversion_operator_exists_between_VotingQuestion_and_Question()
    {
        var votingType = typeof(VotingQuestion);
        var questionType = typeof(Question);

        bool ConvertsBetween(Type type) => type
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name is "op_Implicit" or "op_Explicit")
            .Any(m => (m.ReturnType == questionType && m.GetParameters()[0].ParameterType == votingType)
                   || (m.ReturnType == votingType && m.GetParameters()[0].ParameterType == questionType));

        Assert.False(ConvertsBetween(votingType));
        Assert.False(ConvertsBetween(questionType));
    }
}
