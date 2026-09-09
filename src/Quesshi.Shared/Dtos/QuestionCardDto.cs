namespace Quesshi.Shared;

/// <summary>
/// What a player sees while answering. Deliberately carries no correct answer.
/// <para>
/// <see cref="Kind"/> is a <c>QuestionKind</c> ordinal — 0 choice, 1 sort, 2 map — and exists
/// because without it a client has no way to choose a renderer: until three kinds existed the shape
/// was implied by there being only one. It crosses as an int for the same reason
/// <see cref="Level"/> does: <c>Quesshi.Shared</c> references no other project of ours, so the wire
/// contract stays free of the domain.
/// </para>
/// <para>
/// What each kind puts in <see cref="Choices"/>, and what stays behind: a choice question serves its
/// options as stored; a sorting question serves its items <i>shuffled</i> by
/// <c>Question.ServedChoices</c>, never in stored order and never alongside the permutation that
/// would give it away, because for a sort the stored order is the entire answer; a map question
/// serves none at all and instead carries <see cref="BaseLayer"/> (0 blank, 1 borders) and
/// <see cref="TargetShape"/> (a <c>MapTargetKind</c> ordinal — 0 country, 1 city). The shape, not
/// the target: a country round wants a tap on a region and a city round wants a pin dropped, so the
/// client has to know which interaction to offer, and knowing which of the two it is says nothing
/// about where the answer is. The code and the coordinates appear only at reveal.
/// </para>
/// </summary>
public sealed record QuestionCardDto(int Slot, string QuestionId, string Prompt, List<string> Choices,
    string CategoryId, string CategoryName, string CategoryIcon, string CategoryColor, int Level, MediaDto? Media,
    int SecondsLimit, int TotalSlots, int Kind = 0, int? BaseLayer = null, int? TargetShape = null);
