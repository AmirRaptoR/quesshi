using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public class MatchingRulesTests
{
    [Fact]
    public void Fixed_choice_bounds_are_two_and_the_match_participant_ceiling()
    {
        Assert.Equal(MatchingRules.MinFixedChoices, 2);
        Assert.Equal(MatchRules.MaxParticipants, MatchingRules.MaxFixedChoices);
        Assert.Equal(MatchRules.MaxParticipants, 8);
    }
}
