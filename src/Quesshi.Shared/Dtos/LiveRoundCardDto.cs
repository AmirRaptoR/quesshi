namespace Quesshi.Shared;

/// <summary>
/// The question as it goes out at round start. Never carries the correct index — that is the one
/// redaction live keeps, the same discipline as an async match's <c>QuestionCardDto</c>.
/// <para>
/// <see cref="Kind"/>, <see cref="BaseLayer"/> and <see cref="TargetShape"/> mean exactly what they
/// mean on <see cref="QuestionCardDto"/>, and are redacted to exactly the same line: shuffled items
/// for a sort, a base layer and a target <i>shape</i> for a map, and never the stored order or the
/// target itself. The two card contracts are kept identical here on purpose — a client that can
/// render one round can render the other, and a rule that held in one place and not the other would
/// be a leak waiting for whichever duel kind was forgotten.
/// </para>
/// </summary>
public sealed record LiveRoundCardDto(int Slot, int TotalRounds, string QuestionId, string Prompt, List<string> Choices,
    string CategoryId, string CategoryName, string CategoryIcon, string CategoryColor, int Level, MediaDto? Media,
    DateTimeOffset StartedAt, DateTimeOffset EndsAt, int Kind = 0, int? BaseLayer = null, int? TargetShape = null);
