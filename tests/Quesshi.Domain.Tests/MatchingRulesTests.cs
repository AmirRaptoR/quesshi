using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public class MatchingRulesTests
{
    [Fact]
    public void Matching_keeps_its_eight_choice_and_participant_ceiling()
    {
        Assert.Equal(2, MatchingRules.MinFixedChoices);
        Assert.Equal(8, MatchingRules.MaxFixedChoices);
        Assert.Equal(MatchingRules.MaxParticipants, MatchingRules.MaxFixedChoices);
        Assert.Equal(500, MatchRules.MaxParticipants);
    }
}
