namespace Quesshi.Shared;

/// <summary>ChoiceIndex is -1 when the clock ran out before the player picked anything.</summary>
public sealed record AnswerDto(int Slot, int ChoiceIndex);
