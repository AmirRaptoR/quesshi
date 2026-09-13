using System.Text.Json;

namespace Quesshi.Web.Tests;

public sealed class MatchingPlayerSurfaceTests
{
    [Fact]
    public void Matching_player_has_a_dedicated_route_and_redaction_safe_surface()
    {
        var page = File.ReadAllText(Source("Pages/Matching.razor"));

        Assert.Contains("@page \"/matching/{MatchId}\"", page);
        Assert.Contains("MatchingAsync(MatchId)", page);
        Assert.Contains("MatchingSlotClosed", page);
        Assert.Contains("AnsweredParticipantIds", page);
        Assert.Contains("matching.answer.noAnswer", page);
        Assert.Contains("matching.answer.notApplicable", page);
        Assert.DoesNotContain("<Ring", page);
        Assert.DoesNotContain("Sounds", page);
        Assert.DoesNotContain("Score", page);
        Assert.DoesNotContain("Correct", page);
    }

    [Fact]
    public void Matching_player_supports_reanswer_reload_and_no_contest()
    {
        var page = File.ReadAllText(Source("Pages/Matching.razor"));

        Assert.Contains("Api.AnswerMatchingAsync", page);
        Assert.Contains("_results = _view?.Results", page);
        Assert.DoesNotContain("MatchingResultsAsync", page);
        Assert.Contains("MatchingResultsText.NoContest", page);
        Assert.Contains("_view = updated", page);
        Assert.Contains("participant.Active", page);
        Assert.Contains("result.Counts", page);
        Assert.Contains("waitingFor", page);
        Assert.Contains("a.PlayerId == participant.Id", page);
        Assert.Contains("a.PlayerId == MeId", page);
        Assert.Contains("option.AvatarSeed", page);
        Assert.Contains("ClosedSlots", page);
        Assert.Contains("@foreach (var closed in ClosedSlots)", page);
        Assert.Contains("<MediaBlock Media=\"media\"", page);
    }

    [Fact]
    public void Matching_lobby_exposes_mode_choice_and_owner_start_surface()
    {
        var home = File.ReadAllText(Source("Pages/Home.razor"));
        var lobby = File.ReadAllText(Source("Pages/MatchingLobby.razor"));
        var settings = File.ReadAllText(Source("Pages/MatchingSettings.razor"));

        Assert.Contains("home.modeMatching", home);
        Assert.Contains("/play/matching", home);
        Assert.DoesNotContain("CreateMatchingLobbyAsync", home);
        Assert.Contains("/matching/lobby/", home);
        Assert.Contains("StartMatchingAsync", lobby);
        Assert.Contains("UpdateMatchingSettingsAsync", File.ReadAllText(Source("Services/Api.cs")));
        Assert.Contains("MatchingCategoriesAsync", File.ReadAllText(Source("Services/Api.cs")));
        Assert.Contains("ToggleCategory", lobby);
        Assert.Contains("_categoryIds", lobby);
        Assert.Contains("SelectedCategoryText", lobby);
        Assert.Contains("participant.AvatarSeed", lobby);
        Assert.Contains("CreateMatchingLobbyAsync", settings);
        Assert.Contains("MatchingCategoriesAsync", settings);
        Assert.DoesNotContain("DifficultyRange", settings);
        Assert.DoesNotContain("LastDuelSettings", settings);
    }

    [Fact]
    public void Matching_translation_keys_are_present_in_all_browser_languages()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "i18n");
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, "en.json")))!;
        var keys = en.Keys.Where(k => k.StartsWith("matching.") || k is "home.duelMode" or "home.modeTrivia" or "home.modeMatching");
        foreach (var lang in new[] { "fa", "nl" })
        {
            var table = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, $"{lang}.json")))!;
            Assert.Empty(keys.Except(table.Keys));
        }
    }

    private static string Source(string relative) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Quesshi.Web", relative));
}
