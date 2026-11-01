using Quesshi.Web.Plugins.Formats.Live;

namespace Quesshi.Web.Tests.Live;

/// <summary>Writing a finishing place out as an ordinal per language, as the live standings show it.</summary>
public class RankOrdinalTests
{
    [Theory]
    [InlineData(1, "1st")]
    [InlineData(2, "2nd")]
    [InlineData(3, "3rd")]
    [InlineData(4, "4th")]
    [InlineData(11, "11th")]
    [InlineData(12, "12th")]
    [InlineData(13, "13th")]
    [InlineData(21, "21st")]
    [InlineData(22, "22nd")]
    [InlineData(23, "23rd")]
    [InlineData(111, "111th")]
    public void English_ordinals_follow_the_st_nd_rd_th_rule_including_the_11_13_exception(int rank, string expected)
    {
        Assert.Equal(expected, RankOrdinal.Format(rank.ToString(), rank, "en"));
    }

    [Fact]
    public void Dutch_always_uses_the_colloquial_e_suffix()
    {
        Assert.Equal("2e", RankOrdinal.Format("2", 2, "nl"));
        Assert.Equal("11e", RankOrdinal.Format("11", 11, "nl"));
    }

    [Fact]
    public void Persian_appends_the_ordinal_marker_after_whatever_digits_it_is_given()
    {
        Assert.Equal("۲م", RankOrdinal.Format("۲", 2, "fa"));
    }
}
