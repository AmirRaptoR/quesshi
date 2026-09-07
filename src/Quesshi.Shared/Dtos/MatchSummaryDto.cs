namespace Quesshi.Shared;

public sealed record MatchSummaryDto(string Id, string Code, string Lang, string State, PlayerSideDto Me, PlayerSideDto? Opponent,
    string? WinnerId, bool IsDraw, DateTimeOffset CreatedAt, bool CanPlay, bool CanReveal, string Outcome,
    /// <summary>How many questions this duel holds; not every duel is six.</summary>
    int Questions = 6,
    /// <summary>A live duel is never CanPlay — it advances on its own clock, so the row offers Rejoin instead.</summary>
    bool IsLive = false);
