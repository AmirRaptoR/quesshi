namespace Quesshi.Domain;

/// <summary>
/// What a duel's lobby owner has picked, before any question is drawn: language, length, and which
/// categories and difficulty levels the question set may be built from. This lives on the match from
/// the moment it is created — a lobby that can change these before the duel starts has to have
/// somewhere to keep them — and the actual question set is drawn from whatever this says at the
/// instant the duel starts, not at creation.
/// </summary>
/// <remarks>
/// One language per duel: a lobby can change which one before it starts, but nobody plays a duel
/// mixing two. Empty <paramref name="CategoryIds"/>/<paramref name="Levels"/> mean "no restriction",
/// mirroring how the question set builder already treats an empty filter elsewhere.
/// </remarks>
public sealed record DuelSettings(
    Language Language,
    int QuestionCount,
    IReadOnlyList<string> CategoryIds,
    IReadOnlyList<Difficulty> Levels)
{
    /// <summary>
    /// The validated way to build settings. <see cref="MatchRules.IsValidCount"/> used to guard
    /// <c>LiveMatch.Create</c>/<c>Match.Create</c> directly, back when the question count arrived
    /// with the finished question set; now that the set is drawn later, from whatever settings say
    /// at that instant, an invalid count has to be rejected here, as it is typed, rather than
    /// discovered when the duel begins.
    /// </summary>
    public static DuelSettings Create(Language language, int questionCount, IReadOnlyList<string> categoryIds, IReadOnlyList<Difficulty> levels)
    {
        if (!MatchRules.IsValidCount(questionCount))
            throw new ArgumentException(
                $"A duel needs one of {string.Join(", ", MatchRules.QuestionCountChoices)} questions, got {questionCount}.", nameof(questionCount));

        return new DuelSettings(language, questionCount, categoryIds, levels);
    }

    /// <summary>
    /// Hand-written, because the record-synthesized <c>Equals</c> compares <see cref="CategoryIds"/>
    /// and <see cref="Levels"/> by the declared <c>IReadOnlyList&lt;T&gt;</c> type, which has no
    /// <c>IEquatable</c> of its own and so falls back to reference equality on whatever concrete list
    /// happens to be passed — meaning two independently built (but content-identical) instances, the
    /// normal shape of anything that has crossed an HTTP request, would never compare equal. That
    /// silently breaks the equal-settings no-op rule <c>UpdateSettingsAsync</c> relies on to let a
    /// capacity-only change through once the question set is drawn (issue #104).
    /// </summary>
    public bool Equals(DuelSettings? other) =>
        other is not null && Language == other.Language && QuestionCount == other.QuestionCount
        && CategoryIds.SequenceEqual(other.CategoryIds) && Levels.SequenceEqual(other.Levels);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Language);
        hash.Add(QuestionCount);
        foreach (var id in CategoryIds) hash.Add(id);
        foreach (var level in Levels) hash.Add(level);
        return hash.ToHashCode();
    }
}
