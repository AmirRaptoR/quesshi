using Quesshi.Domain;

namespace Quesshi.Application.Ports;

/// <summary>
/// The question as it goes out at round start. Never carries <c>CorrectIndex</c> — that is the one
/// redaction live keeps, the same discipline as <c>GameEndpoints.cs:221</c>.
/// </summary>
public sealed record LiveRoundCard(
    int Slot, int TotalRounds, string QuestionId, string Prompt, List<string> Choices,
    string CategoryId, string CategoryName, string CategoryIcon, string CategoryColor,
    Difficulty Level, MediaRef Media, DateTimeOffset StartedAt, DateTimeOffset EndsAt);
