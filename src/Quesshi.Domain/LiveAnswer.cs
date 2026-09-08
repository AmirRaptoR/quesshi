namespace Quesshi.Domain;

/// <summary>One player's answer in one round of a live duel. A miss is recorded the same shape,
/// with <see cref="ChoiceIndex"/> -1 and <see cref="Score"/> 0.</summary>
public sealed record LiveAnswer(int ChoiceIndex, bool Correct, int Score, double SecondsTaken);
