using Quesshi.Server.StaticPages;

namespace Quesshi.Server.Tests.StaticPages;

public sealed class LanguageNegotiatorTests
{
    private static readonly OfferedLanguage[] Offered = [new("en", "ltr"), new("fa", "rtl"), new("nl", "ltr")];

    [Fact]
    public void The_cookie_wins_over_the_browser_language_when_the_tenant_offers_it()
    {
        var chosen = LanguageNegotiator.Choose(Offered, "en", "fa", "nl,en;q=0.5");

        Assert.Equal(new OfferedLanguage("fa", "rtl"), chosen);
    }

    [Fact]
    public void A_cookie_for_a_language_the_tenant_does_not_offer_falls_back_to_the_browser_language()
    {
        var chosen = LanguageNegotiator.Choose(Offered, "en", "de", "nl");

        Assert.Equal("nl", chosen.Code);
    }

    [Theory]
    [InlineData("fa-IR,fa;q=0.9,en;q=0.8", "fa")]
    [InlineData("de;q=0.9,nl;q=0.8", "nl")]
    [InlineData("en;q=0.2,fa;q=0.9", "fa")]
    [InlineData("fa;q=0,nl;q=0.1", "nl")]
    public void The_browser_language_decides_in_order_of_preference_when_there_is_no_cookie(string header, string expected)
    {
        Assert.Equal(expected, LanguageNegotiator.Choose(Offered, "en", null, header).Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("de,ja;q=0.8")]
    [InlineData("*")]
    [InlineData("not a header;;;")]
    public void The_tenant_default_is_used_when_nothing_offered_matches(string? header)
    {
        Assert.Equal(new OfferedLanguage("fa", "rtl"), LanguageNegotiator.Choose(Offered, "fa", null, header));
    }
}
