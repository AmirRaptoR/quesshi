namespace Quesshi.Grains.Abstractions;

/// <summary>
/// The whole live duel as one record, redacted for whoever asked. Every deadline is absolute
/// server time, not a remaining-seconds count, so a reconnecting client can redraw the right arc
/// without a per-round sync.
///
/// <see cref="Participants"/> replaces the old <c>ChallengerId</c>/<c>OpponentId</c> pair a
/// capacity-2 duel could get away with: <c>Participants[0]</c> is always the lobby's owner, exactly
/// as <c>LiveMatch.Participants</c> itself defines it, and every later seat follows in
/// join order. This type is never persisted — it is rebuilt fresh from <c>LiveMatch</c> on every
/// <c>GetAsync</c>/<c>CreateAsync</c>/<c>CreateLobbyAsync</c> call and handed straight back to the
/// caller in the same process generation that built it — so renumbering its <c>[Id(n)]</c> slots
/// here carries no migration risk: there is no old-shape blob of this exact type sitting in Redis or
/// Mongo anywhere for a newer reader to misinterpret, unlike <c>LiveMatchSnapshot</c> (the actual
/// persisted state, which keeps its legacy fields for exactly that reason).
/// </summary>
[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.LiveView")]
public sealed record LiveView(
    [property: Id(0)] string Id,
    [property: Id(1)] List<string> Participants,
    [property: Id(2)] int State,
    [property: Id(3)] int Phase,
    [property: Id(4)] DateTimeOffset? PhaseEndsAt,
    [property: Id(5)] int RoundIndex,
    [property: Id(6)] int TotalRounds,
    [property: Id(7)] List<LivePlayerView> Players,
    [property: Id(8)] List<LiveRoundResultView> Rounds,
    [property: Id(9)] string? WinnerId,
    [property: Id(10)] bool IsDraw,
    [property: Id(11)] string? AbandonedBy,
    [property: Id(12)] DateTimeOffset CreatedAt,
    [property: Id(13)] DateTimeOffset? EndedAt,
    [property: Id(14)] string Code,
    [property: Id(15)] int Lang,
    /// <summary>The finished duel's ranking, straight off <c>LiveMatch.Standings</c> and empty while it
    /// is still running. Carried rather than reconstructed: a client that loads a duel cold, after it
    /// has already ended, cannot rebuild this from the fields above — <see cref="AbandonedBy"/> names
    /// at most one abandoner, so a duel with two would rank the second by score instead of below the
    /// first. The domain already knows the answer exactly; there is no reason for a reader to guess it.</summary>
    [property: Id(16)] List<LiveStandingView> Standings);
