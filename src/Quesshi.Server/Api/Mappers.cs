using System.Security.Claims;
using Quesshi.Application.Ports;
using Quesshi.Domain;
using Quesshi.Grains.Abstractions;
using Quesshi.Shared;

namespace Quesshi.Server.Api;

public static class Mappers
{
    public static string? PlayerId(this ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier);

    /// <summary>Read from the token, not from storage: the gate has to hold before anything is loaded.</summary>
    public static bool IsGuest(this ClaimsPrincipal user) => user.HasClaim(Auth.TokenIssuer.GuestClaim, "1");

    public static string Code(this Language lang) => lang switch
    {
        Language.Fa => "fa",
        Language.Nl => "nl",
        _ => "en"
    };

    public static Language ToLanguage(this string? code)
        => code?.ToLowerInvariant() switch
        {
            "en" => Language.En,
            "nl" => Language.Nl,
            _ => Language.Fa
        };

    public static StatsDto ToDto(this PlayerStats s) => new(s.Wins, s.Losses, s.Draws, s.Streak, s.BestStreak, s.TotalScore, s.Played);

    public static MeDto ToMeDto(this Player p, List<FriendDto> friends) => new(
        p.Id, p.DisplayName, p.IsGuest ? "" : p.Email, p.AvatarSeed, p.Lang.Code(), p.Stats.ToDto(),
        p.ByCategory.ToDictionary(kv => kv.Key, kv => p.Accuracy(kv.Key)), friends, p.IsGuest);

    public static CategoryDto ToDto(this Category c, Language lang)
        => new(c.Id, c.NameFor(lang), c.NameFa, c.NameEn, c.Icon, c.Color, c.IsActive, c.SortOrder, c.NameNl);

    public static AdminQuestionDto ToAdminDto(this Question q) => new(
        q.Id, q.Lang.Code(), q.CategoryId, (int)q.Level, q.Prompt, [.. q.Choices], q.CorrectIndex, q.Explanation,
        q.Status.ToString().ToLowerInvariant(), q.Source.ToString().ToLowerInvariant(),
        q.Media.Kind == MediaKind.None ? null : new MediaDto(q.Media.Kind.ToString().ToLowerInvariant(), q.Media.Url, q.Media.Attribution),
        q.CreatedAt, q.TimesServed, q.TimesCorrect,
        q.ReportCount,
        [.. q.Reports.Select(r => new QuestionReportDto(r.PlayerId, "", r.Reason.ToString().ToLowerInvariant(), r.At))]);

    public static AdminUserDto ToAdminDto(this Player p)
        => new(p.Id, p.DisplayName, p.Email, p.AvatarSeed, p.IsBanned, p.Stats.ToDto(), p.CreatedAt);

    public static GenerationRunDto ToDto(this GenerationRun r)
        => new(r.Id, r.StartedAt, r.FinishedAt, r.Requested, r.Inserted, r.Rejected, r.Error);

    public static AiSpendDto ToDto(this AiSpend s)
        => new(s.Calls, s.PromptTokens, s.CompletionTokens, s.Cost);

    /// <summary>
    /// Turns the grain's view into what this player is allowed to see. The grain has already
    /// redacted the opponent's answers; this only decides wording and what the UI may offer.
    /// </summary>
    public static MatchSummaryDto ToSummary(this MatchView v, string me, Func<string, (string Name, string Avatar)> lookup)
    {
        var myRun = v.Runs.FirstOrDefault(r => r.PlayerId == me);
        var otherId = v.ChallengerId == me ? v.OpponentId : v.ChallengerId;
        var theirRun = otherId is null ? null : v.Runs.FirstOrDefault(r => r.PlayerId == otherId);

        var (myName, myAvatar) = lookup(me);
        var mine = new PlayerSideDto(me, myName, myAvatar, myRun?.Score ?? 0, myRun?.Correct ?? 0, myRun?.Answered ?? 0, myRun?.Finished ?? false);

        PlayerSideDto? theirs = null;
        if (otherId is not null)
        {
            var (name, avatar) = lookup(otherId);
            theirs = new PlayerSideDto(otherId, name, avatar, theirRun?.Score ?? 0, theirRun?.Correct ?? 0, theirRun?.Answered ?? 0, theirRun?.Finished ?? false);
        }

        var state = (MatchState)v.State;
        var over = state is MatchState.Resolved or MatchState.Forfeited;
        var canReveal = mine.Finished || over;

        var outcome = !over ? "pending"
            : v.IsDraw ? "draw"
            : v.WinnerId == me ? "win"
            : v.WinnerId is null ? "draw" : "loss";

        return new MatchSummaryDto(v.Id, v.Code, ((Language)v.Lang).Code(), state.ToString().ToLowerInvariant(),
            mine, theirs, v.WinnerId, v.IsDraw, v.CreatedAt, !over && !mine.Finished, canReveal, outcome,
            v.QuestionIds.Count);
    }
}
