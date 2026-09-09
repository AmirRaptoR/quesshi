namespace Quesshi.Shared;

/// <summary>
/// One player's answer within a live round, as redacted for whoever asked. <see cref="Response"/>
/// holds a sorting or map answer, which <see cref="ChoiceIndex"/> cannot, and is hidden and shown on
/// precisely the same terms as it: another player's stays out of the payload until the round closes.
/// </summary>
public sealed record LiveRoundAnswerViewDto(string PlayerId, bool Answered, int? ChoiceIndex, bool? Correct, int Score,
    string? Response = null);
