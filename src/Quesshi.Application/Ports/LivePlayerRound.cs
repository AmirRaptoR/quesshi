namespace Quesshi.Application.Ports;

/// <summary>
/// One player's answer to a revealed round. <see cref="Response"/> carries what
/// <see cref="ChoiceIndex"/> cannot — a sorting answer or a map answer — in exactly the form it was
/// stored in: a stored-index order such as <c>"2,0,3,1"</c>, or a country code or <c>"lat,lon"</c>.
/// <para>
/// Stored-index order is the readable one here even though the player answered in served positions,
/// because the reveal alongside it carries <c>LiveRoundReveal.CorrectOrder</c> — the items in stored
/// order — so <c>"2,0,3,1"</c> indexes straight into that list and comes out as the four items in
/// the order this player actually placed them. The seed is not needed and deliberately not used: the
/// submission path already normalised the answer into stored-index terms, so nothing that merely
/// reads an answer can disagree with what was graded.
/// </para>
/// </summary>
public sealed record LivePlayerRound(string PlayerId, int ChoiceIndex, bool Correct, int RoundScore, int TotalScore,
    string? Response = null);
