namespace Quesshi.Shared;

/// <summary>
/// One player's answer within a round, as redacted for whoever asked. <c>Answered</c> lets a client
/// draw "they have locked in" even while <c>ChoiceIndex</c>/<c>Correct</c> stay hidden.
/// </summary>
public sealed record LiveRoundAnswerViewDto(string PlayerId, bool Answered, int? ChoiceIndex, bool? Correct, int Score);
