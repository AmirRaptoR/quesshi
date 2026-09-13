namespace Quesshi.Domain;

/// <summary>
/// A piece of matching content: a prompt and, depending on <see cref="AnswerSource"/>, either no
/// stored choices at all (they are the match's own participants, filled in when served) or an
/// authored list of 2 to <see cref="MatchingRules.MaxFixedChoices"/> choices. There is no correct
/// answer to grade — matching compares who picked whom, it does not mark anyone right or wrong —
/// which is why this is a separate aggregate from <see cref="Question"/> rather than another
/// <see cref="QuestionKind"/>: <see cref="Question"/> is built around exactly the fields a matching
/// question must not have (a correct index, a difficulty, a map target) and validated to reject them.
/// <para>
/// Like <see cref="Question"/>, this knows nothing about a match, a player or serving — see that
/// class's doc for why. It only knows how to be validly authored, edited and rehydrated.
/// </para>
/// </summary>
public sealed class MatchingQuestion
{
    private MatchingQuestion(string id, Language lang, string matchingCategoryId, string prompt,
        MatchingAnswerSource answerSource, IReadOnlyList<string> fixedChoices, MediaRef media,
        DateTimeOffset createdAt)
    {
        Id = id;
        Lang = lang;
        MatchingCategoryId = matchingCategoryId;
        Prompt = prompt;
        AnswerSource = answerSource;
        FixedChoices = fixedChoices;
        Media = media;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public string Id { get; }
    public Language Lang { get; private set; }
    public string MatchingCategoryId { get; private set; }
    public string Prompt { get; private set; }
    public MatchingAnswerSource AnswerSource { get; private set; }

    /// <summary>The authored choices, for <see cref="MatchingAnswerSource.Fixed"/>. Empty for
    /// <see cref="MatchingAnswerSource.Participants"/> — there is nothing stored to fill in, the
    /// match's own players are substituted in when the question is served.</summary>
    public IReadOnlyList<string> FixedChoices { get; private set; }

    public MediaRef Media { get; private set; }
    public QuestionStatus Status { get; private set; } = QuestionStatus.Pending;
    public QuestionSource Source { get; private set; } = QuestionSource.Admin;

    /// <summary>Subject and aspect, as <see cref="TopicKey"/> builds it. Whatever the caller
    /// supplies; never computed here. Null means "not deduplicated".</summary>
    public string? Topic { get; private set; }

    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public int TimesServed { get; private set; }

    public bool IsPlayable => Status == QuestionStatus.Approved;

    /// <summary>
    /// Trust boundary: everything here arrives from an admin form. Mirrors
    /// <see cref="Question.Create"/>'s split from <see cref="Restore"/>: this validates, that trusts
    /// storage.
    /// </summary>
    public static MatchingQuestion Create(string id, Language lang, string matchingCategoryId, string prompt,
        MatchingAnswerSource answerSource, IReadOnlyList<string>? choices, DateTimeOffset now,
        MediaRef? media = null, QuestionSource source = QuestionSource.Admin,
        QuestionStatus status = QuestionStatus.Pending, string? topic = null)
    {
        Validate(prompt, answerSource, choices);
        var fixedChoices = choices ?? [];
        return new MatchingQuestion(id, lang, matchingCategoryId, prompt.Trim(), answerSource,
            [.. fixedChoices.Select(c => c.Trim())], media ?? MediaRef.None, now)
        {
            Source = source,
            Status = status,
            Topic = topic
        };
    }

    /// <summary>Rehydrates a stored question. Storage is trusted; use <see cref="Create"/> for
    /// anything else.</summary>
    public static MatchingQuestion Restore(string id, Language lang, string matchingCategoryId, string prompt,
        MatchingAnswerSource answerSource, IReadOnlyList<string>? choices, MediaRef media,
        QuestionStatus status, QuestionSource source, string? topic,
        DateTimeOffset createdAt, DateTimeOffset updatedAt,
        int timesServed)
    {
        return new MatchingQuestion(id, lang, matchingCategoryId, prompt, answerSource, choices ?? [], media,
            createdAt)
        {
            UpdatedAt = updatedAt,
            Topic = topic,
            Status = status,
            Source = source,
            TimesServed = timesServed
        };
    }

    /// <summary>
    /// The rules are per answer source, and each is told what it must <i>not</i> have as well as
    /// what it needs — see <see cref="Question.Validate"/> for why that half is the point.
    /// A null <paramref name="choices"/> is treated as empty rather than dereferenced.
    /// </summary>
    public static void Validate(string prompt, MatchingAnswerSource answerSource, IReadOnlyList<string>? choices)
    {
        if (!Enum.IsDefined(answerSource))
            throw new ArgumentOutOfRangeException(nameof(answerSource), answerSource,
                "The matching answer source is not declared.");

        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("A matching question needs a prompt.", nameof(prompt));

        var list = choices ?? [];

        if (answerSource == MatchingAnswerSource.Participants)
        {
            // The options are the match's own participants, filled in when served — there is
            // nothing here for a fixed list to mean, so it is empty rather than "ignored".
            if (list.Count != 0)
                throw new ArgumentException("A participants-answered question has no fixed choices.", nameof(choices));
            return;
        }

        if (list.Count < MatchingRules.MinFixedChoices || list.Count > MatchingRules.MaxFixedChoices)
            throw new ArgumentException(
                $"A matching question needs {MatchingRules.MinFixedChoices} to {MatchingRules.MaxFixedChoices} choices, got {list.Count}.",
                nameof(choices));
        if (list.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Choices cannot be blank.", nameof(choices));
        if (list.Select(c => c.Trim().ToLowerInvariant()).Distinct().Count() != list.Count)
            throw new ArgumentException("Choices must be distinct.", nameof(choices));
    }

    /// <summary>
    /// Replaces the wording, filing and answer source, validating first so a rejected edit changes
    /// nothing. Unlike <see cref="Question.Edit"/>, this takes an explicit <paramref name="now"/> —
    /// see the class's technical notes for why matching content tracks when it last changed and
    /// trivia does not.
    /// </summary>
    public void Edit(Language lang, string matchingCategoryId, string prompt, MatchingAnswerSource answerSource,
        IReadOnlyList<string>? choices, MediaRef? media, string? topic, DateTimeOffset now)
    {
        Validate(prompt, answerSource, choices);
        var fixedChoices = choices ?? [];

        Lang = lang;
        MatchingCategoryId = matchingCategoryId;
        Prompt = prompt.Trim();
        AnswerSource = answerSource;
        FixedChoices = [.. fixedChoices.Select(c => c.Trim())];
        Media = media ?? MediaRef.None;
        Topic = topic;
        UpdatedAt = now;
    }

    /// <summary>Any status, in any direction — an approved question can be sent back for review.</summary>
    public void SetStatus(QuestionStatus status) => Status = status;

    /// <summary>Counts one slot served, not one participant seeing it — a match that serves this
    /// question once to eight players counts once. There is no correctness to record; matching has
    /// nothing to be right about.</summary>
    public void RecordServed() => TimesServed++;
}
