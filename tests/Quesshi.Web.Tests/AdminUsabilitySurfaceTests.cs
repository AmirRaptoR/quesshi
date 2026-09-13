namespace Quesshi.Web.Tests;

public sealed class AdminUsabilitySurfaceTests
{
    [Theory]
    [InlineData("Questions.razor")]
    [InlineData("MatchingQuestions.razor")]
    public void Question_pages_put_generation_import_and_editing_in_shared_modals(string name)
    {
        var page = File.ReadAllText(Page(name));

        Assert.Contains("<AdminShell", page);
        Assert.Contains("<AdminToolbar", page);
        Assert.Contains("_dialog == \"generate\"", page);
        Assert.Contains("_dialog == \"import\"", page);
        Assert.Contains("<AdminModal", page);
    }

    [Theory]
    [InlineData("Categories.razor")]
    [InlineData("MatchingCategories.razor")]
    public void Category_pages_use_the_shared_admin_table_and_modal_editor(string name)
    {
        var page = File.ReadAllText(Page(name));

        Assert.Contains("<AdminShell", page);
        Assert.Contains("<AdminCategoryTable", page);
        Assert.Contains("<AdminModal", page);
    }

    [Fact]
    public void Account_commands_are_separate_from_page_navigation()
    {
        var nav = File.ReadAllText(Source("Components/AdminNav.razor"));

        Assert.Contains("admin-nav__pages", nav);
        Assert.Contains("admin-account__menu", nav);
        Assert.Contains("/admin/password", nav);
        Assert.Contains("SignOutAsync", nav);
    }

    private static string Page(string name) => Source($"Pages/Admin/{name}");
    private static string Source(string relative) => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../src/Quesshi.Web", relative));
}
