namespace Quesshi.Shared;

/// <summary>
/// One player's answer to a revealed round. <see cref="Response"/> is the sorting or map answer
/// <see cref="ChoiceIndex"/> cannot hold, exactly as it was stored: a stored-index order such as
/// <c>"2,0,3,1"</c>, or a country code or <c>"lat,lon"</c>. A sorting response indexes into the
/// reveal's <c>CorrectOrder</c>, which is the list it is expressed against.
/// </summary>
public sealed record LivePlayerRoundDto(string PlayerId, int ChoiceIndex, bool Correct, int RoundScore, int TotalScore,
    string? Response = null);
