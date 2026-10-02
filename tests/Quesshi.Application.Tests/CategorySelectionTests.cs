using Quesshi.Application.UseCases;
using Quesshi.Domain;

namespace Quesshi.Application.Tests;

public sealed class CategorySelectionTests
{
    [Fact]
    public void Player_picks_are_intersected_with_active_family_categories_and_capped()
    {
        var eligible = new[]
        {
            new Category("a", "a", "a", "*", "#fff"),
            new Category("b", "b", "b", "*", "#fff"),
            new Category("c", "c", "c", "*", "#fff"),
            new Category("d", "d", "d", "*", "#fff"),
            new Category("inactive", "i", "i", "*", "#fff", false)
        };

        var selected = CategorySelection.Select(eligible, ["outside", "a", "b", "a", "c", "d", "inactive"]);

        Assert.Equal(["a", "b", "c"], selected);
    }
}
