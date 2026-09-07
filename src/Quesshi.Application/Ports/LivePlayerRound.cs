namespace Quesshi.Application.Ports;

public sealed record LivePlayerRound(string PlayerId, int ChoiceIndex, bool Correct, int RoundScore, int TotalScore);
