namespace Quesshi.Grains.Abstractions;

/// <summary>One player's answer within a round, as redacted for the caller. <c>Answered</c> lets a
/// client draw "they have locked in" even while <c>ChoiceIndex</c>/<c>Correct</c> stay hidden.
/// <para>
/// <see cref="Response"/> is the sorting or map answer <see cref="ChoiceIndex"/> cannot hold, and it
/// is hidden and shown on precisely the same terms: it comes off the same answer record, which the
/// builder only reaches for once the round is closed or the answer is the asking player's own. A
/// sorting answer is in stored-index terms and indexes into the round's <c>CorrectOrder</c>.
/// </para></summary>
[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.LiveRoundAnswerView")]
public sealed record LiveRoundAnswerView(
    [property: Id(0)] string PlayerId,
    [property: Id(1)] bool Answered,
    [property: Id(2)] int? ChoiceIndex,
    [property: Id(3)] bool? Correct,
    [property: Id(4)] int Score,
    [property: Id(5)] string? Response = null);
