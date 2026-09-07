namespace Quesshi.Application.Ports;

public sealed record LivePlayerScore(string PlayerId, int Score, int Correct);
