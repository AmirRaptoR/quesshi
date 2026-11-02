using Quesshi.Formats.Live;
using Quesshi.Web.Plugins.Formats.Live;

namespace Quesshi.Web.Tests;

public class LivePhaseSelectorTests
{
    [Theory]
    [InlineData("lobby", LiveBody.Lobby)]
    [InlineData("idle", LiveBody.Lobby)]
    [InlineData("countdown", LiveBody.Countdown)]
    [InlineData("question", LiveBody.Question)]
    [InlineData("reveal", LiveBody.Reveal)]
    [InlineData("over", LiveBody.Ended)]
    [InlineData("Over", LiveBody.Ended)]
    public void Each_wire_phase_selects_its_own_body(string phase, LiveBody body)
        => Assert.Equal(body, LivePhaseSelector.Select(phase));

    [Fact]
    public void The_numeric_phases_the_server_writes_select_the_same_bodies_as_their_words()
    {
        Assert.Equal(LiveBody.Countdown, LivePhaseSelector.Select(LiveFormat.CountdownPhase.ToString()));
        Assert.Equal(LiveBody.Question, LivePhaseSelector.Select(LiveFormat.QuestionPhase.ToString()));
        Assert.Equal(LiveBody.Reveal, LivePhaseSelector.Select(LiveFormat.RevealPhase.ToString()));
    }

    [Fact]
    public void An_unknown_phase_word_throws_rather_than_silently_picking_a_body()
        => Assert.Throws<ArgumentOutOfRangeException>(() => LivePhaseSelector.Select("nonsense"));
}
