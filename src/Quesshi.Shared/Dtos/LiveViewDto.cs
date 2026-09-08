namespace Quesshi.Shared;

/// <summary>
/// The whole live duel as one record, redacted for whoever asked. Every deadline is absolute server
/// time, not a remaining-seconds count, so a reconnecting client can redraw the right arc with no
/// per-round sync. <c>ServerNow</c> is a wire-only addition beside <c>PhaseEndsAt</c>: it is what
/// lets a client measure clock skew once at connect and never re-sync.
///
/// <see cref="Participants"/> is issue #53's rework of what used to be four flat
/// <c>ChallengerName</c>/<c>ChallengerAvatar</c>/<c>OpponentName</c>/<c>OpponentAvatar</c> fields (the
/// #13 contract additions) plus the <c>ChallengerId</c>/<c>OpponentId</c> pair itself: a capacity-2
/// duel now reads as a two-entry list instead of two pairs of scalars, and a capacity&gt;2 duel is
/// simply a longer one — there is no third named seat this record still cannot describe.
/// <see cref="LobbyEndsAt"/>, <see cref="CurrentCard"/> and <see cref="CurrentExplanation"/> remain
/// exactly what they always were: without them a cold load or a mid-phase reconnect has no
/// code-expiry to count down and no prompt to render.
/// </summary>
public sealed record LiveViewDto(
    string Id, List<LiveParticipantDto> Participants, string State, string Phase,
    DateTimeOffset? PhaseEndsAt, DateTimeOffset ServerNow, int RoundIndex, int TotalRounds,
    List<LivePlayerViewDto> Players, List<LiveRoundResultViewDto> Rounds,
    /// <summary>
    /// One row per participant once the duel is over, empty otherwise — see <see cref="StandingRowDto"/>'s
    /// own remarks. A client connected when the duel ends always has the true ranking, pushed by
    /// <c>LiveEndedDto.Standings</c> straight off <c>LiveMatch.Standings</c>; this field is what a cold
    /// load or a rejoin after the fact reads instead, reconstructed by <c>Mappers.ToLiveDtoAsync</c> from
    /// whatever the redacted <c>LiveView</c> still carries. That reconstruction is a best-effort
    /// approximation for more than one abandoner (see that method's own remarks for why), never the
    /// primary source of truth for a connected client.
    /// </summary>
    List<StandingRowDto> Standings,
    string? WinnerId, bool IsDraw,
    /// <summary>Every abandoner, in the order the domain recorded them — see <c>LiveView.AbandonedBy</c>'s
    /// own remarks for why a single id used to be lossy for anything past a two-player duel's first
    /// quitter.</summary>
    List<string> AbandonedBy, DateTimeOffset CreatedAt, DateTimeOffset? EndedAt,
    string Code = "",
    DateTimeOffset? LobbyEndsAt = null,
    LiveRoundCardDto? CurrentCard = null,
    string? CurrentExplanation = null,
    /// <summary>How many seats this lobby has — issue #53's lobby page addition, fixed at creation
    /// and unrelated to <see cref="Participants"/>.Count, which only ever names the seats actually
    /// filled so far. Defaulted to 2 for callers built before this field existed.</summary>
    int Capacity = 2,
    /// <summary>What the owner picked (or, for a legacy record, what can be reconstructed of it —
    /// see <c>DuelSettingsDto</c>'s own remarks). Never null on a live duel: every <c>LiveMatch</c>
    /// carries a <c>Settings</c> from creation, lobby or not.</summary>
    DuelSettingsDto? Settings = null,
    /// <summary>Mirrors the one existing rule this whole page is built around: settings stop being
    /// editable once the question set is drawn (<c>QuestionIds</c> non-empty), which is exactly
    /// <see cref="TotalRounds"/> &gt; 0 — see <c>LiveMatchGrain</c>'s own <c>ViewAsync</c>, where
    /// <c>TotalRounds</c> is literally <c>QuestionIds.Count</c>.</summary>
    bool SettingsLocked = false);
