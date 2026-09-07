using Quesshi.Shared;

namespace Quesshi.Web.Services;

/// <summary>One side of the duel bar: whoever it is, by the time this is built "mine" and "theirs" have already been decided.</summary>
public sealed record DuelBarSide(string PlayerId, string Name, string Avatar, int Score);

/// <summary>
/// Which side of <see cref="LiveViewDto"/> is "you" and which is "them" depends only on who is
/// asking — the DTO itself has no such notion, it only has a challenger and an opponent. Kept as a
/// plain mapper so the duel-bar acceptance criterion is unit-tested without bUnit.
/// </summary>
public static class DuelBarScores
{
    public static (DuelBarSide Mine, DuelBarSide Theirs) Split(LiveViewDto view, string meId)
    {
        var iAmChallenger = view.ChallengerId == meId;
        var theirId = iAmChallenger ? view.OpponentId : view.ChallengerId;

        var mine = new DuelBarSide(meId,
            iAmChallenger ? view.ChallengerName : view.OpponentName ?? "",
            iAmChallenger ? view.ChallengerAvatar : view.OpponentAvatar ?? "",
            Score(view, meId));

        var theirs = new DuelBarSide(theirId ?? "",
            iAmChallenger ? view.OpponentName ?? "" : view.ChallengerName,
            iAmChallenger ? view.OpponentAvatar ?? "" : view.ChallengerAvatar,
            theirId is null ? 0 : Score(view, theirId));

        return (mine, theirs);
    }

    private static int Score(LiveViewDto view, string playerId)
        => view.Players.FirstOrDefault(p => p.PlayerId == playerId)?.Score ?? 0;
}
