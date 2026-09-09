namespace Quesshi.Domain;

/// <summary>
/// One answer in an async run. <see cref="Response"/> carries what
/// <see cref="AnswerRecord.ChoiceIndex"/> cannot: a stored-index order for a sorting question,
/// <c>"DE"</c> or <c>"52.37,4.9"</c> for a map one. It is last and optional, so an old snapshot
/// deserialises with it null and reads as the <see cref="QuestionKind.Choice"/> answer it always
/// was — no migration, no backfill.
/// <para>
/// <see cref="ChoiceIndex"/> is -1 for a sort or map answer, reusing the sentinel a timeout already
/// uses. That collides only on paper: a timed-out choice answer is -1 with a null response, a sort
/// or map answer is -1 with one, and a timed-out sort or map is -1 with null again — the question's
/// <see cref="QuestionKind"/> plus the response tells them apart, and nothing has to become nullable.
/// </para>
/// </summary>
public sealed record AnswerRecord(int Slot, int ChoiceIndex, bool Correct, int Score, double SecondsTaken,
    string? Response = null);
