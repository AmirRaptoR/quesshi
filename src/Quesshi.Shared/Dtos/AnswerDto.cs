namespace Quesshi.Shared;

/// <summary>
/// One submitted answer. <see cref="ChoiceIndex"/> is -1 when the clock ran out before the player
/// picked anything — and also for every sorting and map answer, which have no choice to point at and
/// travel in <see cref="Response"/> instead.
/// <para>
/// The two are told apart by the response, never by the index alone: a timed-out answer of any kind
/// is -1 with a null <see cref="Response"/>, a played sorting or map answer is -1 with one. A sorting
/// response lists <b>served positions in the order the player placed them</b> — <c>"2,0,3,1"</c>
/// means "the item you showed me third goes first" — and the server inverts the round's shuffle
/// before storing it; a map response is a country code (<c>"DE"</c>) or a point
/// (<c>"52.37,4.9"</c>), always written in the invariant culture.
/// </para>
/// <para>
/// Last and optional, so a client that never sends it — and every choice answer, which never needs
/// it — puts exactly the JSON on the wire it always did.
/// </para>
/// </summary>
public sealed record AnswerDto(int Slot, int ChoiceIndex, string? Response = null);
