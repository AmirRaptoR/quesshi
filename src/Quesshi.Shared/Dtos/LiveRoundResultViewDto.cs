namespace Quesshi.Shared;

/// <summary>
/// One round of a live duel as a reconnecting client gets it back. <see cref="CorrectIndex"/> is the
/// round's answer for a <c>Choice</c> question and is null while the round is still in flight;
/// <see cref="CorrectOrder"/> and <see cref="CorrectTarget"/> are the same answer for a sort and a
/// map, redacted by the same rule and at the same moment, so a round in flight is as blank for the
/// new kinds as it always was for the old one.
/// <para>
/// <see cref="Kind"/> is a <c>QuestionKind</c> ordinal, and it is filled in for the rounds already
/// finished. The round in flight keeps the default, because the server does not so much as load that
/// round's question while it is open; the kind of the round being played comes from the card
/// (<c>LiveViewDto.CurrentCard</c>, or the round-start push), which carries it.
/// </para>
/// </summary>
public sealed record LiveRoundResultViewDto(int Slot, string QuestionId, DateTimeOffset StartedAt, int? CorrectIndex,
    List<LiveRoundAnswerViewDto> Answers, int Kind = 0, List<string>? CorrectOrder = null, string? CorrectTarget = null);
