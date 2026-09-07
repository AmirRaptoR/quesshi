namespace Quesshi.Shared;

public sealed record LivePlayerRoundDto(string PlayerId, int ChoiceIndex, bool Correct, int RoundScore, int TotalScore);
