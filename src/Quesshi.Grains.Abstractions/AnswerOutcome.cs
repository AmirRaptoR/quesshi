namespace Quesshi.Grains.Abstractions;

/// <summary>
/// What an async answer comes back as. <see cref="CorrectIndex"/> is the answer to a
/// <c>QuestionKind.Choice</c> question and only to one; the other two kinds have their own field,
/// and <see cref="Kind"/> says which to read.
/// <para>
/// <see cref="Kind"/> crosses as an <c>int</c> for the reason the rest of this project does:
/// <c>Quesshi.Grains.Abstractions</c> keeps its Orleans-SDK-only reference list and deliberately
/// never references <c>Quesshi.Domain</c>, so a domain enum travels as its ordinal (see
/// <see cref="ILiveMatchGrain.JoinAsync"/>'s own remarks).
/// </para>
/// <para>
/// <see cref="CorrectOrder"/> is a sorting question's items in their correct order, as text: the
/// card served them shuffled and never said which stored index each came from, so indices would
/// mean nothing to the caller. <see cref="CorrectTarget"/> is a map question's target as the answer
/// string that would have hit it — <c>"DE"</c>, or <c>"52.37,4.9"</c>.
/// </para>
/// </summary>
[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.AnswerOutcome")]
public sealed record AnswerOutcome(
    [property: Id(0)] bool Correct,
    [property: Id(1)] int CorrectIndex,
    [property: Id(2)] int Score,
    [property: Id(3)] string? Explanation,
    [property: Id(4)] bool RunFinished,
    [property: Id(5)] int RunScore,
    [property: Id(6)] int Kind = 0,
    [property: Id(7)] List<string>? CorrectOrder = null,
    [property: Id(8)] string? CorrectTarget = null);
