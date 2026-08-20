namespace Quesshi.Domain;

/// <summary>
/// A multiple-choice question. Always exactly <see cref="MatchRules.ChoicesPerQuestion"/> options with
/// exactly one correct. Media is optional; the prompt must stand on its own without it.
/// </summary>
public sealed class Question
{
    /// <summary>
    /// How many separate players have to flag a question before it stops being served. One
    /// disagreement is not evidence; three people independently saying it is wrong usually is.
    /// </summary>
    public const int ReportsBeforeSuppressed = 3;

    private readonly List<QuestionReport> _reports = [];

    private Question(string id, Language lang, string categoryId, Difficulty level, string prompt,
        IReadOnlyList<string> choices, int correctIndex, MediaRef media, DateTimeOffset createdAt)
    {
        Id = id;
        Lang = lang;
        CategoryId = categoryId;
        Level = level;
        Prompt = prompt;
        Choices = choices;
        CorrectIndex = correctIndex;
        Media = media;
        CreatedAt = createdAt;
    }

    public string Id { get; }
    public Language Lang { get; private set; }
    public string CategoryId { get; private set; }
    public Difficulty Level { get; private set; }
    public string Prompt { get; private set; }
    public IReadOnlyList<string> Choices { get; private set; }
    public int CorrectIndex { get; private set; }
    public MediaRef Media { get; private set; }
    public string? Explanation { get; private set; }

    /// <summary>
    /// Subject and aspect, as <see cref="TopicKey"/> builds it. Unique per language in the store,
    /// so the same question cannot be written twice in one language however it is worded. Null on
    /// everything written before the generator started supplying it.
    /// </summary>
    public string? Topic { get; private set; }
    public QuestionStatus Status { get; private set; } = QuestionStatus.Pending;
    public QuestionSource Source { get; private set; } = QuestionSource.Ai;
    public DateTimeOffset CreatedAt { get; }
    public int TimesServed { get; private set; }
    public int TimesCorrect { get; private set; }

    public IReadOnlyList<QuestionReport> Reports => _reports;
    public int ReportCount => _reports.Count;

    public bool IsPlayable => Status == QuestionStatus.Approved && ReportCount < ReportsBeforeSuppressed;

    public static Question Create(string id, Language lang, string categoryId, Difficulty level, string prompt,
        IReadOnlyList<string> choices, int correctIndex, DateTimeOffset now,
        MediaRef? media = null, string? explanation = null,
        QuestionSource source = QuestionSource.Ai, QuestionStatus status = QuestionStatus.Pending,
        string? topic = null)
    {
        Validate(prompt, choices, correctIndex);
        return new Question(id, lang, categoryId, level, prompt.Trim(), [.. choices.Select(c => c.Trim())], correctIndex,
            media ?? MediaRef.None, now)
        { Explanation = explanation, Source = source, Status = status, Topic = topic };
    }

    /// <summary>Rehydrates a stored question. Storage is trusted; use <see cref="Create"/> for anything else.</summary>
    public static Question Restore(string id, Language lang, string categoryId, Difficulty level, string prompt,
        IReadOnlyList<string> choices, int correctIndex, MediaRef media, string? explanation,
        QuestionStatus status, QuestionSource source, DateTimeOffset createdAt, int timesServed, int timesCorrect,
        IEnumerable<QuestionReport>? reports = null, string? topic = null)
    {
        var question = new Question(id, lang, categoryId, level, prompt, choices, correctIndex, media, createdAt)
        {
            Explanation = explanation,
            Topic = topic,
            Status = status,
            Source = source,
            TimesServed = timesServed,
            TimesCorrect = timesCorrect
        };

        if (reports is not null) question._reports.AddRange(reports);
        return question;
    }

    /// <summary>Trust boundary: everything here arrives from a language model or an admin form.</summary>
    public static void Validate(string prompt, IReadOnlyList<string> choices, int correctIndex)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("A question needs a prompt.", nameof(prompt));
        if (choices.Count != MatchRules.ChoicesPerQuestion)
            throw new ArgumentException($"A question needs exactly {MatchRules.ChoicesPerQuestion} choices, got {choices.Count}.", nameof(choices));
        if (choices.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Choices cannot be blank.", nameof(choices));
        if (choices.Select(c => c.Trim().ToLowerInvariant()).Distinct().Count() != choices.Count)
            throw new ArgumentException("Choices must be distinct.", nameof(choices));
        if (correctIndex < 0 || correctIndex >= choices.Count)
            throw new ArgumentOutOfRangeException(nameof(correctIndex), "The correct answer must be one of the choices.");
    }

    public bool IsCorrect(int choiceIndex) => choiceIndex == CorrectIndex;

    public void Approve() => SetStatus(QuestionStatus.Approved);
    public void Reject() => SetStatus(QuestionStatus.Rejected);

    /// <summary>Any status, in any direction — an approved question can be sent back for review.</summary>
    public void SetStatus(QuestionStatus status) => Status = status;

    /// <summary>
    /// Replaces everything about the question except its identity, its status and its history.
    /// Language, category and level are included because mis-filing is the most common mistake an
    /// admin needs to correct, and validation runs first so a rejected edit changes nothing.
    /// </summary>
    public void Edit(Language lang, string categoryId, Difficulty level, string prompt,
        IReadOnlyList<string> choices, int correctIndex, MediaRef? media, string? explanation)
    {
        Validate(prompt, choices, correctIndex);

        Lang = lang;
        CategoryId = categoryId;
        Level = level;
        Prompt = prompt.Trim();
        Choices = [.. choices.Select(c => c.Trim())];
        CorrectIndex = correctIndex;
        Media = media ?? MediaRef.None;
        Explanation = explanation;
    }

    /// <summary>
    /// Records a player's complaint. Returns false if that player has already reported this
    /// question — one person cannot bury a question on their own.
    /// </summary>
    public bool Report(string playerId, ReportReason reason, DateTimeOffset now)
    {
        if (_reports.Any(r => r.PlayerId == playerId)) return false;

        _reports.Add(new QuestionReport(playerId, reason, now));

        // Enough complaints and it leaves play at once, rather than waiting for someone to look.
        if (ReportCount >= ReportsBeforeSuppressed && Status == QuestionStatus.Approved)
            Status = QuestionStatus.Pending;

        return true;
    }

    /// <summary>Clears the complaints after a human has looked. Does not change the status.</summary>
    public void DismissReports() => _reports.Clear();

    public void RecordServed(bool correct)
    {
        TimesServed++;
        if (correct) TimesCorrect++;
    }
}
