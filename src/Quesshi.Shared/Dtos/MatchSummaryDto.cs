namespace Quesshi.Shared;

public sealed record MatchSummaryDto(string Id, string Code, string Lang, string State, PlayerSideDto Me, PlayerSideDto? Opponent,
    string? WinnerId, bool IsDraw, DateTimeOffset CreatedAt, bool CanPlay, bool CanReveal, string Outcome,
    /// <summary>How many questions this duel holds; not every duel is six.</summary>
    int Questions = 6,
    /// <summary>A live duel is never CanPlay — it advances on its own clock, so the row offers Rejoin instead.</summary>
    bool IsLive = false,
    /// <summary>
    /// Issue #53's lobby page addition: null everywhere this DTO was already used (the duels list,
    /// a single 1v1's <c>Me</c>/<c>Opponent</c> summary once revealed) and populated only by
    /// <c>GameEndpoints.ToSummaryAsync</c>, which the lobby page's <c>GET /api/matches/{id}</c> call
    /// goes through. Every seat, in join order — <see cref="LiveParticipantDto"/> already carries
    /// name/avatar/guest for this without a second lookup type — so a capacity&gt;2 async lobby's
    /// roster is not stuck describing only <see cref="Me"/> and one <see cref="Opponent"/>.
    /// </summary>
    List<LiveParticipantDto>? Participants = null,
    /// <summary>How many seats this lobby has. 2 for every summary minted before this field existed —
    /// the only capacity a duel could ever have back then.</summary>
    int Capacity = 2,
    /// <summary>What the owner picked; see <c>DuelSettingsDto</c>'s own remarks. Null only for a
    /// summary minted before this field existed and never re-fetched.</summary>
    DuelSettingsDto? Settings = null,
    /// <summary>Settings stop being editable once the question set is drawn — <see cref="Questions"/>
    /// itself is that drawn count, so this is simply <see cref="Questions"/> &gt; 0.</summary>
    bool SettingsLocked = false);
