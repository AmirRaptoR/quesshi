using Quesshi.Domain;

namespace Quesshi.Application.Ports;

/// <summary>
/// The answer to a round, for both players at once. <c>EndsAt</c> is when the reveal gives way to
/// the next round.
/// <para>
/// One answer field per kind, because no single one of them can say what the other two mean:
/// <see cref="CorrectIndex"/> is the answer to a <see cref="QuestionKind.Choice"/> round and
/// nothing at all to the other two, <see cref="CorrectOrder"/> is a
/// <see cref="QuestionKind.Sort"/> round's items in their correct order, and
/// <see cref="CorrectTarget"/> is a <see cref="QuestionKind.Map"/> round's target as an answer
/// string. <see cref="Kind"/> is what says which one to read.
/// </para>
/// <para>
/// <see cref="CorrectOrder"/> is the items as text rather than a list of indices on purpose: the
/// card the player was just looking at served those items <i>shuffled</i>, and never told them which
/// stored index each one came from, so an index-shaped answer would be a number they cannot map onto
/// anything they saw. It doubles as the index space each player's own <c>Response</c> is expressed
/// in — see <c>LivePlayerRound.Response</c>.
/// </para>
/// </summary>
public sealed record LiveRoundReveal(int Slot, int CorrectIndex, string? Explanation, List<LivePlayerRound> Players,
    DateTimeOffset EndsAt, QuestionKind Kind = QuestionKind.Choice, List<string>? CorrectOrder = null,
    string? CorrectTarget = null);
