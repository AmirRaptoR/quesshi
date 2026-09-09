using Quesshi.Domain;

namespace Quesshi.Application.Ports;

/// <summary>
/// The question as it goes out at round start. Never carries <c>CorrectIndex</c> — that is the one
/// redaction live keeps, the same discipline as <c>GameEndpoints.cs:221</c>.
/// <para>
/// That discipline had to grow with the kinds rather than bend for them. <see cref="Choices"/> is
/// what <c>Question.ServedChoices</c> returned — the options as stored for a
/// <see cref="QuestionKind.Choice"/> question, the items <i>shuffled</i> for a
/// <see cref="QuestionKind.Sort"/> one (the stored order is that question's whole answer, so it can
/// never go out), and empty for a <see cref="QuestionKind.Map"/> one. A map round carries its
/// <see cref="BaseLayer"/> and the <i>shape</i> of its target and not the target itself: a client
/// cannot tell a "tap the country" round from a "drop a pin" round out of <see cref="Kind"/> alone,
/// but knowing which of the two it is reveals nothing about where the answer lies.
/// </para>
/// </summary>
public sealed record LiveRoundCard(
    int Slot, int TotalRounds, string QuestionId, string Prompt, List<string> Choices,
    string CategoryId, string CategoryName, string CategoryIcon, string CategoryColor,
    Difficulty Level, MediaRef Media, DateTimeOffset StartedAt, DateTimeOffset EndsAt,
    QuestionKind Kind = QuestionKind.Choice, MapBaseLayer? BaseLayer = null, MapTargetKind? TargetShape = null);
