namespace Quesshi.Grains.Abstractions;

/// <summary>
/// One round as a participant sees it. For the round still open, <c>CorrectIndex</c> is null and
/// so is every answer's <c>ChoiceIndex</c>/<c>Correct</c> except the asking player's own.
/// <para>
/// The two kind-specific answers below are redacted by exactly the same rule and at exactly the
/// same moment as <see cref="CorrectIndex"/>: all three stay null while the round is in flight, and
/// a <c>Sort</c> or <c>Map</c> round in flight is therefore as blank as a <c>Choice</c> one. They
/// exist because <see cref="CorrectIndex"/> cannot express either — see <c>LiveRoundReveal</c>, whose
/// shape this mirrors so that the live push and a reconnect's history can never disagree about what
/// the answer to a round was.
/// </para>
/// </summary>
[GenerateSerializer]
[Alias("Quesshi.Grains.Abstractions.LiveRoundResultView")]
public sealed record LiveRoundResultView(
    [property: Id(0)] int Slot,
    [property: Id(1)] string QuestionId,
    [property: Id(2)] DateTimeOffset StartedAt,
    [property: Id(3)] int? CorrectIndex,
    [property: Id(4)] List<LiveRoundAnswerView> Answers,
    /// <summary>
    /// A <c>QuestionKind</c> ordinal — the domain enum crosses as an int, as everything else in this
    /// project does. It stays at its <c>Choice</c> default for the round still in flight, because the
    /// builder deliberately never loads that round's question at all: not loading it is the strongest
    /// form of "cannot leak it". A client does not need it from here either — the card it was pushed
    /// (or the one a cold load hands it as <c>CurrentCard</c>) carries the kind of the round being
    /// played, and this field is for the rounds already behind it.
    /// </summary>
    [property: Id(5)] int Kind = 0,
    /// <summary>A sorting round's items in their correct order. Null until the round closes.</summary>
    [property: Id(6)] List<string>? CorrectOrder = null,
    /// <summary>A map round's target as an answer string. Null until the round closes.</summary>
    [property: Id(7)] string? CorrectTarget = null);
