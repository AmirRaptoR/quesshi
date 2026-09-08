namespace Quesshi.Shared;

/// <summary>The wire shape of <c>LiveHub.Rematch</c>'s return value. <c>Status</c> is one of
/// "refused", "waiting", "created" or "failed"; only "created" carries a <c>NewMatchId</c> and
/// <c>NewMatchCode</c> — the latter is the same share code every other participant receives on
/// <c>RematchCreatedDto</c>, handed straight back to whoever pressed the button so their own client
/// never has to wait for that push to show the link.</summary>
public sealed record RematchOutcomeDto(string Status, string? NewMatchId = null, string? NewMatchCode = null);
