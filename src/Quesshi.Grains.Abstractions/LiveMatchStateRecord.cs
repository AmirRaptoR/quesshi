namespace Quesshi.Grains.Abstractions;

/// <summary>The whole live duel, as JSON. See the ponytail note on MatchGrain.</summary>
[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.LiveMatchStateRecord")]
public sealed class LiveMatchStateRecord
{
    [Id(0)] public string Json { get; set; } = "";

    /// <summary>
    /// Which participant has pressed Rematch, and when — a flag, not a counter, so a second press by
    /// the same player is a no-op. Kept beside the match's own JSON rather than inside it: readiness
    /// is a grain-level handshake concern, not part of the duel <see cref="LiveMatchSnapshot"/> replays.
    /// </summary>
    [Id(1)] public Dictionary<string, DateTimeOffset> RematchReadyAt { get; set; } = [];

    /// <summary>
    /// Which participants' effects (result, answer stats, abandonment penalty) have already been
    /// applied by <c>LiveMatchGrain.SettleAsync</c> for this duel — an optimisation, not a safety
    /// mechanism: <c>Player.TryRecordSettledMatch</c>'s own dedup marker on the player document is
    /// what actually guarantees each participant is settled at most once, so this set only saves
    /// re-walking players already done on a resume. Its loss corrupts nothing; it can be updated
    /// after each participant's effect rather than atomically with it.
    /// </summary>
    [Id(2)] public HashSet<string> SettledPlayers { get; set; } = [];

    /// <summary>
    /// Tri-state, and the default matters more than anything else in this file: <c>null</c> means no
    /// settlement block was ever written for this record at all — either the duel has not ended yet,
    /// or, just as likely, it ended before this field existed and was settled by that older code the
    /// moment it did. Every finished duel ever played still has grain state sitting in Redis (nothing
    /// calls <c>ClearStateAsync</c>), so a naive default of <c>false</c> here would read every one of
    /// them as "over, and not yet settled" the instant it is next activated — a code-triggered
    /// mass double-settlement, not a resume. So <c>null</c> is read as "already settled, never touch
    /// again", <c>false</c> as "settlement started under the new code and did not finish — resume
    /// it", and <c>true</c> as "finished". Only a duel that ends after this field ships ever sees it
    /// move at all, which is exactly the set of duels the new settlement code is responsible for.
    /// </summary>
    [Id(3)] public bool? SettlementComplete { get; set; }
}
