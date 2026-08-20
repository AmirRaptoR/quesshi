namespace Quesshi.Domain;

public sealed record AnswerRecord(int Slot, int ChoiceIndex, bool Correct, int Score, double SecondsTaken);
