using Quesshi.Shared;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

/// <summary>Issue #91's "1,215 points · 2nd overall" line: finding a rank on the top-20 leaderboard
/// (or not — the unranked case), and writing whatever rank is found out as an ordinal per language.</summary>
public class RankOrdinalTests
{
    private static LeaderboardRowDto Row(int rank, string playerId) => new(rank, playerId, playerId, "seed", 0);

    [Fact]
    public void A_player_on_the_board_gets_their_own_row_s_rank()
    {
        List<LeaderboardRowDto> board = [Row(1, "sara"), Row(2, "amir")];

        Assert.Equal(2, RankOrdinal.Rank(board, "amir"));
    }

    /// <summary>The top-20 cutoff (GameEndpoints' own `board.TopAsync(20)`) is the entire reason an
    /// unranked player exists at all — nobody outside it has a known position to show.</summary>
    [Fact]
    public void A_player_who_never_cracked_the_top_20_has_no_rank_at_all()
    {
        List<LeaderboardRowDto> board = [Row(1, "sara")];

        Assert.Null(RankOrdinal.Rank(board, "amir"));
    }

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
        // Translator.Num would already have swapped these to Persian digits by the time this runs in
        // Profile.razor — this only proves the marker lands after them unchanged, not the digit swap.
        Assert.Equal("۲م", RankOrdinal.Format("۲", 2, "fa"));
    }
}
