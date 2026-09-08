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

    /// <summary>
    /// The whole of <c>PUT /api/me</c>'s write, moved here (issue #54) because that endpoint used to
    /// call <c>players.UpsertAsync</c> directly — a full-document write racing this grain's own, so a
    /// duel settling moments after a rename could silently overwrite it with the pre-rename copy still
    /// held in this grain's own cache. Routing the write through the grain that owns the
    /// cache is what makes that race impossible rather than merely unlikely: there is now exactly one
    /// in-memory copy of this player, and every mutation — a settlement or a profile edit — goes
    /// through it in turn.
    ///
    /// <paramref name="lang"/> crosses as a nullable-free <c>int</c> for the same reason
    /// <see cref="SettleMatchAsync"/>'s <c>outcome</c> does: this project does not reference
    /// <c>Quesshi.Domain</c>. <paramref name="avatarSeed"/> null means "leave the avatar as it is" —
    /// <c>UpdateProfileDto</c>'s own convention — and is not validated here; the caller
    /// (<c>GameEndpoints</c>) checks it against the offered palette before ever reaching the grain, the
    /// same division <c>Rename</c>'s length check already draws. Returns false, changing nothing, for
    /// a player id with no record — the caller's cue to answer as unauthorized rather than trust a
    /// write that did not happen.
    /// </summary>
    [Alias("UpdateProfileAsync")]
    Task<bool> UpdateProfileAsync(string displayName, int lang, string? avatarSeed);

    /// <summary>
    /// <c>POST /admin/users/{id}/ban</c>'s write, moved here for the same reason as
    /// <see cref="UpdateProfileAsync"/> and for a sharper consequence: writing a ban straight to the
    /// repository left it one settled match away from being silently lifted, since that settlement's
    /// own grain-cached copy of the player still had <c>IsBanned = false</c> and would upsert right
    /// over the admin's write. Two admin writes racing each other on the same player — a ban and a
    /// profile correction, say — are serialised by the same grain activation instead of by whichever
    /// Mongo write happens to land last. Returns false, changing nothing, for a player id with no
    /// record.
    /// </summary>
    [Alias("SetBannedAsync")]
    Task<bool> SetBannedAsync(bool banned);
}
