namespace Quesshi.Shared;

/// <summary>The answer to a round, for both players at once. <c>EndsAt</c> is when the reveal gives way to the next round.
/// <para>
/// <see cref="Kind"/>, <see cref="CorrectOrder"/> and <see cref="CorrectTarget"/> carry the same
/// per-kind answer <see cref="AnswerResultDto"/> does, in the same shapes, so that the live reveal
/// and the async one can be rendered by the same code. Each player's own sorting or map answer rides
/// on <see cref="LivePlayerRoundDto.Response"/>.
/// </para>
/// </summary>
public sealed record LiveRoundRevealDto(int Slot, int CorrectIndex, string? Explanation, List<LivePlayerRoundDto> Players,
    DateTimeOffset EndsAt, int Kind = 0, List<string>? CorrectOrder = null, string? CorrectTarget = null);
