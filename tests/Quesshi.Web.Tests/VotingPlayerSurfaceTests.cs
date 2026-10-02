using System.Text.Json;

namespace Quesshi.Web.Tests;

public sealed class VotingPlayerSurfaceTests
{
    [Fact]
    public void Voting_player_has_a_dedicated_route_and_redaction_safe_surface()
    {
        var page = File.ReadAllText(Source("Pages/Voting.razor"));

        Assert.Contains("@page \"/voting/{MatchId}\"", page);
        Assert.Contains("VotingAsync(MatchId)", page);
        Assert.Contains("VotingSlotClosed", page);
        Assert.Contains("AnsweredParticipantIds", page);
        Assert.Contains("voting.answer.noAnswer", page);
        Assert.Contains("voting.answer.notApplicable", page);
        Assert.Contains("voting.answer.multiplePeople", page);
        Assert.Contains("voting.answer.both", page);
        Assert.Contains("voting.answer.noOne", page);
        Assert.DoesNotContain("<Ring", page);
        Assert.DoesNotContain("Sounds", page);
        Assert.DoesNotContain("Score", page);
        Assert.DoesNotContain("Correct", page);
    }

    [Fact]
    public void Voting_player_supports_reanswer_reload_and_no_contest()
    {
        var page = File.ReadAllText(Source("Pages/Voting.razor"));

        Assert.Contains("Api.AnswerVotingAsync", page);
        Assert.Contains("_results = _view?.Results", page);
        Assert.DoesNotContain("VotingResultsAsync", page);
        Assert.Contains("VotingResultsText.NoContest", page);
        Assert.Contains("_view = updated", page);
        Assert.Contains("participant.Active", page);
        Assert.Contains("result.Counts", page);
        Assert.Contains("waitingFor", page);
        Assert.Contains("a.PlayerId == participant.Id", page);
        Assert.Contains("a.PlayerId == MeId", page);
        Assert.Contains("option.AvatarSeed", page);
        Assert.Contains("ClosedSlots", page);
        Assert.Contains("@foreach (var closed in ClosedSlots)", page);
        Assert.Contains("@if (IsFinished)", page);
        Assert.Contains("<MediaBlock Media=\"media\"", page);
    }

    [Fact]
    public void Voting_reuses_the_trivia_lobby_and_only_its_settings_are_mode_specific()
    {
        var home = File.ReadAllText(Source("Pages/Home.razor"));
        var lobby = File.ReadAllText(Source("Pages/Lobby.razor"));
        var settings = File.ReadAllText(Source("Pages/VotingSettings.razor"));
        var layout = File.ReadAllText(Source("Layout/MainLayout.razor"));

        Assert.Contains("home.modeVoting", home);
        Assert.Contains("/play/voting", home);
        Assert.DoesNotContain("CreateVotingLobbyAsync", home);
        Assert.Contains("/voting/lobby/", home);
        Assert.Contains("@page \"/lobby/{Code}\"", lobby);
        Assert.Contains("@page \"/voting/lobby/{Code}\"", lobby);
        Assert.Contains("LobbyPresentation.Seats", lobby);
        Assert.Contains("CopyAsync", lobby);
        Assert.Contains("ShareAsync", lobby);
        Assert.Contains("GuestIdentityForm", lobby);
        Assert.Contains("_friends", lobby);
        Assert.Contains("InviteToVotingLobbyAsync", lobby);
        Assert.Contains("StartVotingAsync", lobby);
        Assert.Contains("not_enough_questions", lobby);
        Assert.Contains("voting.lobby.notEnoughQuestions", lobby);
        Assert.Contains("UpdateVotingSettingsAsync", File.ReadAllText(Source("Services/Api.cs")));
        Assert.Contains("VotingCategoriesAsync", File.ReadAllText(Source("Services/Api.cs")));
        Assert.DoesNotContain("ToggleCategory", lobby);
        Assert.DoesNotContain("_categoryIds", lobby);
        Assert.Contains("CreateVotingLobbyAsync", settings);
        Assert.Contains("UpdateVotingSettingsAsync", settings);
        Assert.Contains("QuestionsDraft.Read", settings);
        Assert.Contains("ToggleCategory", settings);
        Assert.Contains("VotingCategoriesAsync", settings);
        Assert.Contains("\"/play/voting\"", layout);
        Assert.Contains("\"/voting/lobby/\"", layout);
        Assert.DoesNotContain("DifficultyRange", settings);
        Assert.DoesNotContain("LastDuelSettings", settings);
    }

    [Fact]
    public void Home_reads_tenant_modes_and_only_renders_enabled_game_choices()
    {
        var home = File.ReadAllText(Source("Pages/Home.razor"));
        var api = File.ReadAllText(Source("Services/Api.cs"));

        Assert.Contains("TenantModesAsync()", home);
        Assert.Contains("HasAsyncMode", home);
        Assert.Contains("HasLiveMode", home);
        Assert.Contains("HasVotingMode", home);
        Assert.Contains("TenantModesAsync()", api);
    }

    [Fact]
    public void Voting_translation_keys_are_present_in_all_browser_languages()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "i18n");
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, "en.json")))!;
        var keys = en.Keys.Where(k => k.StartsWith("voting.") || k is "home.duelMode" or "home.modeTrivia" or "home.modeVoting");
        foreach (var lang in new[] { "fa", "nl" })
        {
            var table = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, $"{lang}.json")))!;
            Assert.Empty(keys.Except(table.Keys));
        }
    }

    private static string Source(string relative) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Quesshi.Web", relative));
}
