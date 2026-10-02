using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public class VotingRulesTests
{
    [Fact]
    public void Voting_keeps_its_eight_choice_and_participant_ceiling()
    {
        Assert.Equal(2, VotingRules.MinFixedChoices);
        Assert.Equal(8, VotingRules.MaxFixedChoices);
        Assert.Equal(VotingRules.MaxParticipants, VotingRules.MaxFixedChoices);
        Assert.Equal(500, MatchRules.MaxParticipants);
    }
}
