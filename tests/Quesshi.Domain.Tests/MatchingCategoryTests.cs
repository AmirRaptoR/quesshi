using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public class MatchingCategoryTests
{
    private static MatchingCategory New(string nameFa = "فارسی", string nameEn = "English", string nameNl = "Nederlands")
        => new("movies", nameFa, nameEn, "icon", "#fff", NameNl: nameNl);

    [Fact]
    public void NameFor_returns_the_requested_language_when_present()
    {
        var c = New();

        Assert.Equal("فارسی", c.NameFor(Language.Fa));
        Assert.Equal("English", c.NameFor(Language.En));
        Assert.Equal("Nederlands", c.NameFor(Language.Nl));
    }

    [Fact]
    public void NameFor_falls_back_to_English_when_the_requested_language_is_blank()
    {
        var c = New(nameFa: "", nameNl: "");

        Assert.Equal("English", c.NameFor(Language.Fa));
        Assert.Equal("English", c.NameFor(Language.Nl));
    }

    [Fact]
    public void NameFor_falls_back_to_Persian_when_English_is_also_blank()
    {
        var c = New(nameEn: "");

        Assert.Equal("فارسی", c.NameFor(Language.En));
    }
}
