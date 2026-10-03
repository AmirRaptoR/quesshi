using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Infrastructure.Mongo;

namespace Quesshi.Server.Tests;

public sealed class GenerationRunDocTests
{
    [Fact]
    public void Trivia_generation_run_round_trips_with_its_family()
    {
        var startedAt = Millisecond(DateTimeOffset.UtcNow);
        var run = new GenerationRun("trivia", startedAt, null, 4, 3, 1, "partial");

        var document = GenerationRunDoc.From(run);

        Assert.Equal((int)QuestionFamily.Trivia, document.Family);
        Assert.Equal(run, document.ToDomain());
        Assert.Throws<InvalidOperationException>(() => document.ToVoting());
    }

    [Fact]
    public void Voting_generation_run_round_trips_with_its_family_and_prompt_metadata()
    {
        var startedAt = Millisecond(DateTimeOffset.UtcNow);
        var run = new VotingGenerationRun("voting", startedAt, startedAt.AddSeconds(1), Language.Fa,
            "friends", VotingAnswerSource.Fixed, 5, 4, 1, null);

        var document = GenerationRunDoc.From(run);

        Assert.Equal((int)QuestionFamily.Voting, document.Family);
        Assert.Equal(run, document.ToVoting());
        Assert.Throws<InvalidOperationException>(() => document.ToDomain());
    }

    private static DateTimeOffset Millisecond(DateTimeOffset value)
        => new(value.Ticks - value.Ticks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
}
