namespace Quesshi.Shared;

/// <summary>The wire shape of <c>LiveHub.Rematch</c>'s return value. <c>Status</c> is one of
/// "refused", "waiting", "created" or "failed"; only "created" carries a <c>NewMatchId</c>.</summary>
public sealed record RematchOutcomeDto(string Status, string? NewMatchId = null);
