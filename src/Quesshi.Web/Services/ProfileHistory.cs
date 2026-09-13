using Quesshi.Shared;

namespace Quesshi.Web.Services;

public static class ProfileHistory
{
    private static readonly HashSet<string> ActiveStates = new(StringComparer.OrdinalIgnoreCase)
        { "awaitingopponent", "awaiting_opponent", "inprogress", "in_progress", "lobby" };

    public static List<MatchSummaryDto> Completed(IEnumerable<MatchSummaryDto> matches, int take = 12)
        => [.. matches.Where(match => !ActiveStates.Contains(match.State))
            .OrderByDescending(match => match.CreatedAt).Take(take)];

    public static string Route(MatchSummaryDto match)
        => string.Equals(match.Mode, "matching", StringComparison.OrdinalIgnoreCase)
            ? $"/matching/{match.Id}"
            : match.IsLive ? $"/live/{match.Id}" : $"/duel/{match.Id}";

    public static string ModeKey(MatchSummaryDto match)
        => string.Equals(match.Mode, "matching", StringComparison.OrdinalIgnoreCase)
            ? "home.modeMatching"
            : match.IsLive ? "home.live" : "home.modeTrivia";

    public static string OutcomeKey(MatchSummaryDto match) => match.Outcome switch
    {
        "win" => "result.win",
        "loss" => "result.loss",
        "draw" => "result.draw",
        _ => "profile.historyFinished"
    };
}
