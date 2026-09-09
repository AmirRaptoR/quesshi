namespace Quesshi.Shared;

/// <summary>
/// The outcome of one async answer, and the round's own answer with it.
/// <para>
/// One answer field per kind, because none of the three can stand in for the others.
/// <see cref="CorrectIndex"/> keeps its old meaning and keeps it for <c>Choice</c> alone;
/// <see cref="CorrectOrder"/> is a sorting question's items in their correct order;
/// <see cref="CorrectTarget"/> is a map question's target as an answer string — <c>"DE"</c>, or
/// <c>"52.37,4.9"</c>. <see cref="Kind"/> (a <c>QuestionKind</c> ordinal) says which to read.
/// </para>
/// <para>
/// <see cref="CorrectOrder"/> is text, not indices, because the card served the items shuffled and
/// never revealed which stored index each one came from — an index here would point at nothing the
/// player ever saw.
/// </para>
/// </summary>
public sealed record AnswerResultDto(bool Correct, int CorrectIndex, int Score, string? Explanation, bool RunFinished,
    int RunScore, int Kind = 0, List<string>? CorrectOrder = null, string? CorrectTarget = null);
