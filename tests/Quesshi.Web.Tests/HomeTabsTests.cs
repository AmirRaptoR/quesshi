using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

/// <summary>
/// The home's remembered tab (issue #88). <c>AppState</c> itself needs a browser to test — it reads
/// and writes localStorage over JS interop — so the decision it makes is here, where it is a pure
/// function of what came back out of storage, and <c>AppState</c> is left holding only the interop.
/// </summary>
public class HomeTabsTests
{
    [Fact]
    public void A_stored_tab_comes_back_as_itself()
    {
        Assert.Equal(HomeTabs.Live, HomeTabs.Normalise(HomeTabs.Live));
        Assert.Equal(HomeTabs.Offline, HomeTabs.Normalise(HomeTabs.Offline));
    }

    [Fact]
    public void Nothing_stored_opens_on_live()
        => Assert.Equal(HomeTabs.Live, HomeTabs.Normalise(null));

    [Theory]
    [InlineData("")]
    [InlineData("duels")]
    [InlineData("Offline")]
    [InlineData("OFFLINE")]
    public void An_unreadable_value_opens_on_live(string stored)
        => Assert.Equal(HomeTabs.Live, HomeTabs.Normalise(stored));

    /// <summary>The two names are the strings that go into localStorage, so a rename would silently
    /// reset every player's remembered tab — worth a test that says so out loud.</summary>
    [Fact]
    public void The_stored_names_are_lower_case_and_distinct()
    {
        Assert.Equal("live", HomeTabs.Live);
        Assert.Equal("offline", HomeTabs.Offline);
    }
}
