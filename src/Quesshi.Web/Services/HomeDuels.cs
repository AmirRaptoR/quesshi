using Quesshi.Shared;

namespace Quesshi.Web.Services;

/// <summary>
/// The Offline tab's two lists. Issue #87's audit found the old Duels page was one undifferentiated
/// column in which "it is your move" and "there is nothing you can do" looked identical — so the
/// split is the whole point of the screen, and it is decided here rather than inside the markup so
/// it can be proven with a plain list of summaries and no rendered page.
/// </summary>
public sealed record HomeDuelSplit(List<MatchSummaryDto> YourTurn, List<MatchSummaryDto> WaitingOnThem);

public static class HomeDuels
{
    /// <summary>
    /// Async duels only, split by whose move it is. A live duel is never <c>CanPlay</c> — it advances
    /// on its own clock and belongs to the Live tab — so it is dropped here entirely rather than
    /// filed under "waiting on them", where it would read as something this player could wait out.
    /// Newest first within each list, which is the order the old Duels page already used.
    /// </summary>
    public static HomeDuelSplit Split(IEnumerable<MatchSummaryDto> matches)
    {
        var offline = matches.Where(m => !m.IsLive).OrderByDescending(m => m.CreatedAt).ToList();
        return new([.. offline.Where(m => m.CanPlay)], [.. offline.Where(m => !m.CanPlay)]);
    }

    /// <summary>How many cards this player still owes. Clamped at zero because a duel whose question
    /// set shrank after the fact (an admin retiring a question) must not render "-2 questions left".</summary>
    public static int QuestionsLeft(MatchSummaryDto match) => Math.Max(0, match.Questions - match.Me.Answered);

    /// <summary>
    /// Where a row opens. The same two routes <c>MatchRow</c> has always used for an async duel:
    /// <c>/play/{id}</c> while there are cards left for this player, <c>/duel/{id}</c> once there are
    /// not — a duel being waited on is still worth opening to see where it stands, which is what
    /// stops "Waiting on them" being the dead end the audit called out.
    /// </summary>
    public static string Route(MatchSummaryDto match) => match.CanPlay ? $"/play/{match.Id}" : $"/duel/{match.Id}";
}
