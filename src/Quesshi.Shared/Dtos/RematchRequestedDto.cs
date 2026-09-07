namespace Quesshi.Shared;

/// <summary>Pushed once, on the first rematch press, so the still-undecided side's Ended screen can say so.</summary>
public sealed record RematchRequestedDto(string PlayerId);
