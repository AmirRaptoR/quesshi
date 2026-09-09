using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

/// <summary>
/// The nullable response string on both answer shapes, and the sentinel it shares with a timeout.
/// </summary>
public class AnswerResponseTests
{
    [Fact]
    public void An_answer_written_the_old_way_has_no_response()
    {
        var record = new AnswerRecord(0, 2, true, 140, 3.5);
        var live = new LiveAnswer(2, true, 140, 3.5);

        Assert.Null(record.Response);
        Assert.Null(live.Response);
    }

    [Fact]
    public void A_sort_or_map_answer_carries_its_response_beside_the_sentinel()
    {
        var sort = new LiveAnswer(-1, true, 140, 3.5, "0,1,2,3");
        var map = new AnswerRecord(4, -1, false, 0, 9.0, "52.37,4.9");

        Assert.Equal("0,1,2,3", sort.Response);
        Assert.Equal(-1, sort.ChoiceIndex);
        Assert.Equal("52.37,4.9", map.Response);
        Assert.Equal(-1, map.ChoiceIndex);
    }

    [Fact]
    public void A_timeout_and_a_non_choice_answer_are_told_apart_by_the_response()
    {
        var timedOut = new LiveAnswer(-1, false, 0, 20);
        var submitted = new LiveAnswer(-1, false, 0, 8, "1,0,2,3");

        // Both wear the same -1, so the sentinel alone cannot say which is which. The response can:
        // a timed-out answer of any kind has none, an answered sort or map always does.
        Assert.Null(timedOut.Response);
        Assert.NotNull(submitted.Response);
        Assert.NotEqual(timedOut, submitted);
    }
}
