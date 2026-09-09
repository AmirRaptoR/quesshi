namespace Quesshi.Shared;

/// <summary>
/// One thin bucket on the admin dashboard.
/// <para>
/// <see cref="Kind"/> and <see cref="Target"/> arrived together and for one reason. A bucket used to
/// be (language, category, level) and every one of them was measured against a single configured
/// number, so once questions came in three shapes the table showed three identical-looking rows per
/// category — same language, same level, wildly different meanings — all judged against a threshold
/// that only described one of them. The row now says which kind it is and what that kind's target
/// actually is, so "4" can be read as "4 of 6" rather than "4 of 25, alarming".
/// </para>
/// </summary>
/// <param name="Kind">
/// <c>"choice"</c>, <c>"sort"</c> or <c>"map"</c> — the lower-cased <c>QuestionKind</c>, spelled the
/// way every other enum crosses this wire.
/// </param>
/// <param name="Target">How many questions of this kind that bucket should hold.</param>
public sealed record BucketDto(string Lang, string CategoryId, int Level, int Approved, int Pending,
    string Kind = "choice", int Target = 0);
