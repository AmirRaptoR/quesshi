using System.Text.Json;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class MatchingAdminSurfaceTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "i18n");

    [Fact]
    public void Every_matching_admin_error_has_a_translation_in_every_language()
    {
        foreach (var lang in new[] { "en", "fa", "nl" })
        {
            var table = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Combine(Folder, $"{lang}.json")))!;

            var missing = AdminApi.MatchingAdminErrorCodes
                .Where(code => !table.ContainsKey($"admin.err.{code}"))
                .ToArray();
            Assert.Empty(missing);
        }
    }

    [Fact]
    public void Matching_question_page_keeps_matching_fields_out_of_trivia_form()
    {
        var page = File.ReadAllText(Page("MatchingQuestions.razor"));

        Assert.Contains("@inherits AdminPageBase", page);
        Assert.Contains("admin.matching.subject", page);
        Assert.Contains("admin.matching.aspect", page);
        Assert.DoesNotContain("CorrectIndex", page);
        Assert.DoesNotContain("Difficulty", page);
        Assert.Contains("c.Id == form.MatchingCategoryId && !c.IsActive", page);
        Assert.Contains("c.IsActive || c.Id == form.MatchingCategoryId", page);
        Assert.Contains("/matching/questions/import", File.ReadAllText(Source("Services/AdminApi.cs")));
    }

    [Fact]
    public void Matching_admin_navigation_exposes_both_matching_pages()
    {
        var nav = File.ReadAllText(Source("Components/AdminNav.razor"));

        Assert.Contains("/admin/matching/questions", nav);
        Assert.Contains("/admin/matching/categories", nav);
    }

    private static string Page(string name) => Source($"Pages/Admin/{name}");
    private static string Source(string relative) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Quesshi.Web", relative));
}
