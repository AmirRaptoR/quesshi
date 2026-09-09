namespace Quesshi.Shared;

/// <summary>
/// One question of a finished async duel, with both sides' answers — the duel history.
/// <para>
/// <see cref="Kind"/> is a <c>QuestionKind</c> ordinal, and it decides which of the answer fields
/// below carries the answer. For a <c>Choice</c> question it is <see cref="CorrectIndex"/>, and
/// <see cref="MyChoice"/>/<see cref="TheirChoice"/> are the two players'. For a <c>Sort</c> question
/// the answer is <see cref="Choices"/> itself: unlike a card, this contract carries the items in
/// their stored order, and stored order <i>is</i> the correct order — so there is no second copy of
/// them here, and <see cref="MyResponse"/>/<see cref="TheirResponse"/> (<c>"2,0,3,1"</c>) index into
/// that list to give each player's own ordering. For a <c>Map</c> question <see cref="Choices"/> is
/// empty and the answer is <see cref="CorrectTarget"/>, with each player's pin or country in the
/// same two response fields.
/// </para>
/// <para>
/// The response fields exist because <see cref="MyChoice"/> and <see cref="TheirChoice"/> are ints:
/// without them a finished sorting or map round would render as two blanks in the history of a duel
/// both players had in fact answered.
/// </para>
/// </summary>
public sealed record RevealedQuestionDto(int Slot, string QuestionId, string Prompt, List<string> Choices, int CorrectIndex,
    int? MyChoice, int? TheirChoice, string CategoryName, string? Explanation, MediaDto? Media,
    int Kind = 0, string? CorrectTarget = null, string? MyResponse = null, string? TheirResponse = null);
