namespace Quesshi.Domain;

/// <summary>
/// What a question is *about*, independent of how it is worded: a subject and the aspect of it
/// being asked. "Wie regisseerde Inception?" and "Inception werd geregisseerd door wie?" share no
/// content words at all, but both are inception|director.
///
/// The generator supplies both halves. Uniqueness is then enforced per language in the database,
/// which is the only place that can enforce it against writers that do not know about each other.
/// </summary>
public static class TopicKey
{
    /// <summary>
    /// Null when either half is missing — a question with no key is simply not covered by the
    /// unique index, which is what keeps the hand-written seed bank working unchanged.
    /// </summary>
    public static string? From(string? subject, string? aspect)
    {
        var left = PromptFingerprint.Normalise(subject);
        var right = PromptFingerprint.Normalise(aspect);

        return left.Length == 0 || right.Length == 0 ? null : $"{left}|{right}";
    }
}
