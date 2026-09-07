using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public class LivePhaseSelectorTests
{
    [Theory]
    [InlineData("lobby", LiveBody.Lobby)]
    [InlineData("countdown", LiveBody.Countdown)]
    [InlineData("question", LiveBody.Question)]
    [InlineData("reveal", LiveBody.Reveal)]
    [InlineData("over", LiveBody.Ended)]
    [InlineData("Over", LiveBody.Ended)]
    public void Each_wire_phase_selects_its_own_body(string phase, LiveBody body)
        => Assert.Equal(body, LivePhaseSelector.Select(phase));

    [Fact]
    public void An_unknown_phase_word_throws_rather_than_silently_picking_a_body()
        => Assert.Throws<ArgumentOutOfRangeException>(() => LivePhaseSelector.Select("nonsense"));
}
