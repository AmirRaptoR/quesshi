namespace Quesshi.Shared;

/// <summary>Pushed once both sides have pressed Rematch: the fresh duel both clients navigate to.</summary>
public sealed record RematchCreatedDto(string NewMatchId);
