using System.Text.Json;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

public sealed class VotingAdminSurfaceTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "i18n");

    [Fact]
    public void Every_voting_admin_error_has_a_translation_in_every_language()
    {
        foreach (var lang in new[] { "en", "fa", "nl" })
        {
            var table = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Combine(Folder, $"{lang}.json")))!;

            var missing = AdminApi.VotingAdminErrorCodes
                .Where(code => !table.ContainsKey($"admin.err.{code}"))
                .ToArray();
            Assert.Empty(missing);
        }
    }

    [Fact]
    public void Voting_question_page_keeps_voting_fields_out_of_trivia_form()
    {
        var page = File.ReadAllText(Page("VotingQuestions.razor"));

        Assert.Contains("@inherits AdminPageBase", page);
        Assert.Contains("admin.voting.subject", page);
        Assert.Contains("admin.voting.aspect", page);
        Assert.DoesNotContain("CorrectIndex", page);
        Assert.DoesNotContain("Difficulty", page);
        Assert.Contains("c.Id == form.CategoryId && !c.IsActive", page);
        Assert.Contains("c.IsActive || c.Id == form.CategoryId", page);
        var api = File.ReadAllText(Source("Services/AdminApi.cs"));
        Assert.Contains("/voting/questions/import", api);
        Assert.Contains("api/admin/voting/generate", api);
        Assert.Contains("GenerateVotingRequestDto", page);
        Assert.Contains("AnswerSource", page);
        Assert.DoesNotContain("GenerateRequestDto", page);
        Assert.Contains("ReadFromJsonAsync<SaveError>", api);
    }

    [Fact]
    public void Voting_admin_navigation_exposes_voting_questions_and_shared_categories()
    {
        var nav = File.ReadAllText(Source("Components/AdminNav.razor"));

        Assert.Contains("/admin/voting/questions", nav);
        Assert.Contains("/admin/categories", nav);
        Assert.DoesNotContain("/admin/voting/categories", nav);
    }

    private static string Page(string name) => Source($"Pages/Admin/{name}");
    private static string Source(string relative) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Quesshi.Web", relative));
}
