namespace Quesshi.Domain.Tests;

/// <summary>
/// The zero floor on <see cref="PlayerStats.TotalScore"/> lives on the record itself, not on any one
/// caller: a `with` expression that would send it negative has to clamp too, so that
/// <see cref="Player.RecordResult"/> and <see cref="Player.RecordAbandonment"/> can move the score by
/// plain arithmetic with no clamp of their own.
/// </summary>
public class PlayerStatsTests
{
    [Fact]
    public void A_with_expression_that_would_go_negative_floors_at_zero()
    {
        var stats = PlayerStats.Empty with { TotalScore = 50 };

        var penalised = stats with { TotalScore = stats.TotalScore - 200 };

        Assert.Equal(0, penalised.TotalScore);
    }

    [Fact]
    public void Constructing_directly_with_a_negative_score_also_floors_at_zero()
        => Assert.Equal(0, new PlayerStats(0, 0, 0, 0, 0, -50).TotalScore);
}
