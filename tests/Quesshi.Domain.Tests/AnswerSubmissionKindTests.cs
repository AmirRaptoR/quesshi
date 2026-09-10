using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

/// <summary>
/// What the two state machines do with an answer that has no choice index. Both aggregates now take
/// a response beside <c>ChoiceIndex</c>, and <see cref="LiveMatch.Answer"/> additionally takes the
/// question's kind — the one fact it needs to know whether its choice-range rule applies at all.
/// <para>
/// Neither class grades anything: correctness arrives already decided, exactly as it always has, and
/// the normalisation of a sorting answer happens a layer up, in the grain, where the round's shuffle
/// can be reconstructed. What is under test here is only the rule these classes own — which
/// submissions they accept, and what they store.
/// </para>
/// </summary>
public class AnswerSubmissionKindTests
{
    private const string Challenger = "u-amir";
    private const string Opponent = "u-sara";
    private static readonly string[] Ten = [.. Enumerable.Range(1, MatchRules.QuestionsPerMatch).Select(i => $"q{i}")];
    private static readonly DateTimeOffset T0 = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static DuelSettings NewSettings() => DuelSettings.Create(Language.En, MatchRules.QuestionsPerMatch, [], []);

    /// <summary>A two-player duel past its countdown, so round 0 is open for answers.</summary>
    private static LiveMatch InRound0()
    {
        var m = LiveMatch.Create("lm-kind", "CODEK1", Challenger, NewSettings(), capacity: 2, T0);
        m.DrawQuestions(Ten);
        m.Join(Opponent, T0);
        m.Start(Challenger, T0);
        m.Advance(T0 + LiveRules.StartCountdown);
        return m;
    }

    private static Match StartedRun()
    {
        var m = Match.Create("m-kind", "CODEK2", Challenger, NewSettings(), capacity: 2, T0);
        m.DrawQuestions(Ten);
        m.Join(Opponent, T0);
        return m;
    }

    [Theory]
    [InlineData(QuestionKind.Sort, "2,0,3,1")]
    [InlineData(QuestionKind.Map, "DE")]
    public void A_live_answer_with_no_choice_index_is_recorded_when_its_kind_has_none_to_give(QuestionKind kind, string response)
    {
        var m = InRound0();
        var at = m.CurrentRound!.StartedAt + TimeSpan.FromSeconds(4);

        var answer = m.Answer(Challenger, 0, -1, correct: true, at, Difficulty.Medium, kind, response);

        // -1 is not a timeout here, and the range check that would have rejected it belongs to Choice
        // alone: this is a played answer that scored, with the played answer itself in Response.
        Assert.Equal(-1, answer.ChoiceIndex);
        Assert.Equal(response, answer.Response);
        Assert.True(answer.Correct);
        Assert.True(answer.Score > 0);
        Assert.Equal(answer, m.CurrentRound!.Answers[Challenger]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(MatchRules.ChoicesPerQuestion)]
    public void A_live_choice_answer_outside_the_range_is_still_rejected(int choiceIndex)
    {
        var m = InRound0();

        // The rule did not go away, it only became a rule about one kind. A choice question still has
        // exactly four answers and -1 still means the player never picked one.
        Assert.Throws<InvalidOperationException>(() =>
            m.Answer(Challenger, 0, choiceIndex, correct: true, m.CurrentRound!.StartedAt, Difficulty.Medium, QuestionKind.Choice));
    }

    [Fact]
    public void A_live_choice_answer_stores_no_response()
    {
        var m = InRound0();

        var answer = m.Answer(Challenger, 0, 2, correct: true, m.CurrentRound!.StartedAt);

        Assert.Equal(2, answer.ChoiceIndex);
        Assert.Null(answer.Response);
    }

    [Fact]
    public void An_async_answer_stores_the_response_it_was_given()
    {
        var m = StartedRun();
        var served = m.ServeNext(Challenger, T0);

        // Stored-index terms — the shuffle was inverted before this call, and this class neither
        // knows nor needs to know that it ever happened.
        var answer = m.SubmitAnswer(Challenger, served.Index, -1, correct: true, T0.AddSeconds(3),
            Difficulty.Medium, "0,1,2,3");

        Assert.Equal("0,1,2,3", answer.Response);
        Assert.Equal(-1, answer.ChoiceIndex);
        Assert.True(answer.Score > 0);
        Assert.Equal(answer, m.RunOf(Challenger)!.Answers[0]);
    }

    [Fact]
    public void An_async_timeout_is_the_same_sentinel_with_nothing_beside_it()
    {
        var m = StartedRun();
        var served = m.ServeNext(Challenger, T0);

        var answer = m.SubmitAnswer(Challenger, served.Index, -1, correct: false, T0 + MatchRules.QuestionTime);

        // The pair that has to stay distinguishable: this and the played answer above wear the same
        // -1, and only the response tells them apart.
        Assert.Equal(-1, answer.ChoiceIndex);
        Assert.Null(answer.Response);
        Assert.False(answer.Correct);
        Assert.Equal(0, answer.Score);
    }

    [Fact]
    public void An_async_choice_answer_is_unchanged_apart_from_a_null_response()
    {
        var m = StartedRun();
        var served = m.ServeNext(Challenger, T0);

        var answer = m.SubmitAnswer(Challenger, served.Index, 2, correct: true, T0.AddSeconds(3));

        Assert.Equal(new AnswerRecord(0, 2, true, answer.Score, answer.SecondsTaken), answer);
        Assert.Null(answer.Response);
    }
}
