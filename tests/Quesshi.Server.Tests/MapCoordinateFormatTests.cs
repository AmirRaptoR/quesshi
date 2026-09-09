using System.Globalization;
using Quesshi.Domain;
using Quesshi.Shared;

namespace Quesshi.Server.Tests;

/// <summary>
/// A map question's coordinates are written twice in this repository, and this is the test that
/// stops the two from drifting.
/// <para>
/// <c>Quesshi.Domain.Geo</c> is what the server writes a target with and grades an answer against.
/// <c>Quesshi.Shared.WorldMapProjection</c> is what the browser writes a tap with — the browser
/// cannot reach the domain, because <c>Quesshi.Web</c> references only <c>Quesshi.Shared</c>, and
/// inverting that so a wire-contract project depended on the domain would cost far more than one
/// duplicated formatter. So the duplication is deliberate and this is its brace: if the two ever
/// disagree by so much as a decimal place, a player's pin stops matching the target it was compared
/// against, and the only symptom is a map that marks correct answers wrong.
/// </para>
/// </summary>
public class MapCoordinateFormatTests
{
    public static TheoryData<double, double> Points => new()
    {
        { 0, 0 },
        { 52.3676, 4.9041 },      // Amsterdam
        { 35.6892, 51.389 },      // Tehran
        { -33.8688, 151.2093 },   // Sydney
        { 40.7128, -74.006 },     // New York
        { 90, -180 },
        { -90, 180 },
        { 1.0 / 3, -2.0 / 3 },    // more decimals than either side keeps
    };

    [Theory]
    [MemberData(nameof(Points))]
    public void The_browser_and_the_server_write_a_point_identically(double latitude, double longitude)
        => Assert.Equal(Geo.Format(latitude, longitude), WorldMapProjection.FormatPoint(latitude, longitude));

    [Theory]
    [MemberData(nameof(Points))]
    public void Each_side_reads_back_what_the_other_wrote(double latitude, double longitude)
    {
        Assert.True(Geo.TryParse(WorldMapProjection.FormatPoint(latitude, longitude), out var serverLat, out var serverLon));
        Assert.True(WorldMapProjection.TryParsePoint(Geo.Format(latitude, longitude), out var clientLat, out var clientLon));

        Assert.Equal(serverLat, clientLat, 6);
        Assert.Equal(serverLon, clientLon, 6);
    }

    [Theory]
    [InlineData("۵۲.۳۷,۴.۹")]    // Persian digits
    [InlineData("52٫37,4٫9")]    // and a Persian decimal separator
    [InlineData("NaN,4.9")]
    [InlineData("Infinity,4.9")]
    [InlineData("52.37")]
    [InlineData("52.37,4.9,3")]
    [InlineData("91,4.9")]
    [InlineData("52.37,181")]
    public void Both_sides_refuse_the_same_malformed_answers(string text)
    {
        Assert.False(Geo.TryParse(text, out _, out _));
        Assert.False(WorldMapProjection.TryParsePoint(text, out _, out _));
    }

    /// <summary>
    /// The one genuinely ambiguous string, pinned so that both sides stay ambiguous the same way.
    /// <c>"52,37"</c> is how a Persian or German keyboard writes 52.37 — but it is also, read the way
    /// this format is defined, the perfectly ordinary point 52°N 37°E, and the comma has already done
    /// its job as the separator before any number parsing happens. Neither side can tell those two
    /// apart and neither tries; what matters is that they cannot disagree, because a target and an
    /// answer that read the same string differently would grade a right answer wrong.
    /// </summary>
    [Fact]
    public void An_ambiguous_answer_is_read_the_same_way_by_both_sides()
    {
        Assert.True(Geo.TryParse("52,37", out var serverLat, out var serverLon));
        Assert.True(WorldMapProjection.TryParsePoint("52,37", out var clientLat, out var clientLon));

        Assert.Equal(52, serverLat);
        Assert.Equal(37, serverLon);
        Assert.Equal(serverLat, clientLat);
        Assert.Equal(serverLon, clientLon);
    }

    /// <summary>
    /// The whole reason both sides pin the culture. A Blazor client takes the browser's culture, and
    /// most of this app's players are on a Persian one.
    /// </summary>
    [Fact]
    public void Neither_side_changes_its_mind_on_a_Persian_thread()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fa-IR");

            var target = MapTarget.City(52.3676, 4.9041, 50);
            var pin = WorldMapProjection.FormatPoint(52.3676, 4.9041);

            Assert.Equal("52.3676,4.9041", pin);
            Assert.Equal(target.ToResponse(), pin);
            Assert.True(target.Matches(pin));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }
}
