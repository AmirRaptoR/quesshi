using System.Reflection;
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

    [Fact]
    public void NameFor_treats_whitespace_as_a_present_name_and_unknown_language_as_English()
    {
        var c = new MatchingCategory("movies", " ", "en", "icon", "#fff", NameNl: " ");

        Assert.Equal(" ", c.NameFor(Language.Fa));
        Assert.Equal(" ", c.NameFor(Language.Nl));

        var fallback = new MatchingCategory("movies", "فارسی", "", "icon", "#fff");
        Assert.Equal("فارسی", fallback.NameFor((Language)42));
    }

    [Fact]
    public void MatchingCategory_defaults_match_Category()
    {
        var matching = new MatchingCategory("movies", "fa", "en", "icon", "#fff");
        var trivia = new Category("movies", "fa", "en", "icon", "#fff");

        Assert.Equal(trivia.IsActive, matching.IsActive);
        Assert.Equal(trivia.SortOrder, matching.SortOrder);
        Assert.Equal(trivia.NameNl, matching.NameNl);
    }

    [Fact]
    public void MatchingCategory_is_a_distinct_type_from_Category_with_no_conversion()
    {
        var matchingCategoryType = typeof(MatchingCategory);
        var categoryType = typeof(Category);

        Assert.False(categoryType.IsAssignableFrom(matchingCategoryType));
        Assert.False(matchingCategoryType.IsAssignableFrom(categoryType));

        bool ConvertsBetween(Type type) => type
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name is "op_Implicit" or "op_Explicit")
            .Any(m => (m.ReturnType == categoryType && m.GetParameters()[0].ParameterType == matchingCategoryType)
                   || (m.ReturnType == matchingCategoryType && m.GetParameters()[0].ParameterType == categoryType));

        Assert.False(ConvertsBetween(matchingCategoryType));
        Assert.False(ConvertsBetween(categoryType));
    }
}
