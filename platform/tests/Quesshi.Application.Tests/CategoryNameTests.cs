using Quesshi.Application.Content;

namespace Quesshi.Application.Tests;

public sealed class CategoryNameTests
{
    [Fact]
    public void Category_name_uses_the_requested_language_when_it_is_present()
    {
        var category = new CategoryDefinition(Guid.Empty, "history", new Dictionary<string, string>
        {
            ["en"] = "History",
            ["fa"] = "تاریخ",
        }, "book", "#123456", true, 0);

        Assert.Equal("تاریخ", category.NameFor("fa", "en"));
    }

    [Fact]
    public void Category_name_falls_back_to_the_tenant_default_language()
    {
        var category = new CategoryDefinition(Guid.Empty, "history", new Dictionary<string, string>
        {
            ["en"] = "History",
            ["fa"] = "تاریخ",
        }, "book", "#123456", true, 0);

        Assert.Equal("History", category.NameFor("nl", "en"));
    }
}
