using Orleans;

namespace Quesshi.Grains.Abstractions;

[Alias("Quesshi.Grains.Abstractions.IPlayerGrain")]
public interface IPlayerGrain : IGrainWithStringKey
{
    /// <summary>
    /// Everything that happens once, for one player, when a match ends — a single atomic mutation
    /// under one dedup marker, because a settled duel can carry a result, an abandonment penalty, or
    /// both (a quitter is both a loser and a penalised abandoner) and a marker keyed only by match id
    /// cannot guard two separate grain calls without one of them silently no-opping the other.
    ///
    /// <paramref name="outcome"/> crosses the Orleans boundary as a nullable <c>int</c> rather than a
    /// nullable <c>Quesshi.Domain.MatchOutcome</c> — this abstractions project deliberately does not
    /// reference Quesshi.Domain, the same convention this replaced <c>ApplyResultAsync</c> already
    /// followed for the non-nullable case. Null means "no result to record" (an abandoner's opponent still gets
    /// one; a no-contest gets none for anyone), so no answer stats are recorded either. Null
    /// <paramref name="abandonedAt"/> means no abandonment penalty applies; it must be the match's own
    /// end time, not wall-clock, so a retry or a delayed recovery computes the identical penalty every
    /// time. Both effects are guarded by the same settled-match-id dedup, so a repeated call for a
    /// <paramref name="matchId"/> already on record is a no-op for both.
    ///
    /// The leaderboard projection is written unconditionally, after that guard rather than inside it,
    /// so a call that finds the match already settled still repairs a leaderboard whose write failed
    /// last time.
    /// </summary>
    [Alias("SettleMatchAsync")]
    Task SettleMatchAsync(string matchId, int? outcome, int score, List<string> categoryIds, List<bool> correct, DateTimeOffset? abandonedAt);

    [Alias("AddFriendAsync")]
    Task AddFriendAsync(string otherId);
    [Alias("RemoveFriendAsync")]
    Task RemoveFriendAsync(string otherId);
    [Alias("CardAsync")]
    Task<PlayerCard?> CardAsync();
}
