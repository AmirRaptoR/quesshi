namespace Quesshi.Domain;

/// <summary>One player's answer in one round of a live duel. A miss is recorded the same shape,
/// with <see cref="ChoiceIndex"/> -1 and <see cref="Score"/> 0.
/// <para>
/// <see cref="Response"/> is the answer to a question that has no choice index: the stored-index
/// order of a sorting question, or the country code or <c>"lat,lon"</c> of a map one. Those arrive
/// with <see cref="ChoiceIndex"/> at the same -1 the timeout uses — see
/// <see cref="AnswerRecord"/> for why that is not the ambiguity it looks like.
/// </para></summary>
public sealed record LiveAnswer(int ChoiceIndex, bool Correct, int Score, double SecondsTaken,
    string? Response = null);
