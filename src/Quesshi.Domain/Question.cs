using System.Globalization;

namespace Quesshi.Domain;

/// <summary>
/// A question, in one of three shapes. A <see cref="QuestionKind.Choice"/> question is exactly
/// <see cref="MatchRules.ChoicesPerQuestion"/> options with exactly one correct; a
/// <see cref="QuestionKind.Sort"/> question is the same four options stored in their correct order;
/// a <see cref="QuestionKind.Map"/> question has no options at all and carries a
/// <see cref="MapTarget"/> instead. Media is optional; the prompt must stand on its own without it.
/// <para>
/// One class and a discriminator rather than three aggregates, because <c>IQuestionRepository</c>,
/// the unique <c>Lang + Topic</c> index, <see cref="TopicKey"/>, the generator and the admin panel
/// all assume one collection and one shape. Forking them would cost far more than one enum — and
/// since <see cref="QuestionKind.Choice"/> is the default, every question written before this
/// existed is already valid without a migration.
/// </para>
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
    /// <summary>
    /// The options, for <see cref="QuestionKind.Choice"/>; the items <i>in their correct order</i>,
    /// for <see cref="QuestionKind.Sort"/> — which is why a sort card never serves them as stored,
    /// but through <see cref="SortOrder"/>. Empty for <see cref="QuestionKind.Map"/>.
    /// </summary>
    public IReadOnlyList<string> Choices { get; private set; }

    /// <summary>Which choice is right. Meaningful for <see cref="QuestionKind.Choice"/> only; the
    /// other two kinds pin it to 0 rather than leave it holding a number nothing reads.</summary>
    public int CorrectIndex { get; private set; }

    public QuestionKind Kind { get; private set; } = QuestionKind.Choice;

    /// <summary>Where the answer is, for a <see cref="QuestionKind.Map"/> question. Null for the
    /// other two kinds, and validated to be null rather than merely ignored.</summary>
    public MapTarget? Target { get; private set; }

    /// <summary>Which world map this is played on. Null for anything but a map question.</summary>
    public MapBaseLayer? BaseLayer { get; private set; }
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

    /// <summary>
    /// The kind and its two map fields come last and default to the <see cref="QuestionKind.Choice"/>
    /// case, so every call written before sorting and map questions existed still compiles and still
    /// means what it meant.
    /// </summary>
    public static Question Create(string id, Language lang, string categoryId, Difficulty level, string prompt,
        IReadOnlyList<string> choices, int correctIndex, DateTimeOffset now,
        MediaRef? media = null, string? explanation = null,
        QuestionSource source = QuestionSource.Ai, QuestionStatus status = QuestionStatus.Pending,
        string? topic = null, QuestionKind kind = QuestionKind.Choice, MapTarget? target = null,
        MapBaseLayer? baseLayer = null, IReadOnlySet<string>? knownCountryCodes = null)
    {
        Validate(prompt, choices, correctIndex, kind, target, baseLayer, knownCountryCodes);
        return new Question(id, lang, categoryId, level, prompt.Trim(), [.. choices.Select(c => c.Trim())], correctIndex,
            media ?? MediaRef.None, now)
        {
            Explanation = explanation, Source = source, Status = status, Topic = topic,
            Kind = kind, Target = target, BaseLayer = baseLayer
        };
    }

    /// <summary>Rehydrates a stored question. Storage is trusted; use <see cref="Create"/> for anything else.</summary>
    public static Question Restore(string id, Language lang, string categoryId, Difficulty level, string prompt,
        IReadOnlyList<string> choices, int correctIndex, MediaRef media, string? explanation,
        QuestionStatus status, QuestionSource source, DateTimeOffset createdAt, int timesServed, int timesCorrect,
        IEnumerable<QuestionReport>? reports = null, string? topic = null,
        QuestionKind kind = QuestionKind.Choice, MapTarget? target = null, MapBaseLayer? baseLayer = null)
    {
        var question = new Question(id, lang, categoryId, level, prompt, choices, correctIndex, media, createdAt)
        {
            Explanation = explanation,
            Topic = topic,
            Status = status,
            Source = source,
            TimesServed = timesServed,
            TimesCorrect = timesCorrect,
            Kind = kind,
            Target = target,
            BaseLayer = baseLayer
        };

        if (reports is not null) question._reports.AddRange(reports);
        return question;
    }

    /// <summary>
    /// Trust boundary: everything here arrives from a language model or an admin form.
    /// <para>
    /// The rules are per-kind, and each kind is told what it must <i>not</i> have as well as what it
    /// needs. That half is the point rather than padding: a rule that only says what a kind requires
    /// lets a <see cref="QuestionKind.Choice"/> question carry a stray map target that nothing reads
    /// and nothing rejects, and the first sign of it is a question behaving oddly some months later
    /// when someone edits its kind.
    /// </para>
    /// <para>
    /// <paramref name="knownCountryCodes"/> is how the caller lends this method the one fact it
    /// cannot know: which countries the bundled SVG can actually draw. That set belongs to the map
    /// asset, not to the domain, so it is passed in rather than hard-coded here — and when it is not
    /// supplied the code's <i>form</i> is still checked, just not its existence.
    /// </para>
    /// </summary>
    public static void Validate(string prompt, IReadOnlyList<string> choices, int correctIndex,
        QuestionKind kind = QuestionKind.Choice, MapTarget? target = null, MapBaseLayer? baseLayer = null,
        IReadOnlySet<string>? knownCountryCodes = null)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("A question needs a prompt.", nameof(prompt));

        if (kind == QuestionKind.Map)
        {
            // A map question's answer is its target, so choices would be a second answer with no
            // rule tying the two together. Empty, not "ignored".
            if (choices.Count != 0)
                throw new ArgumentException("A map question has no choices.", nameof(choices));
            if (correctIndex != 0)
                throw new ArgumentOutOfRangeException(nameof(correctIndex), correctIndex, "A map question has no correct choice; leave it at 0.");
            if (target is null)
                throw new ArgumentException("A map question needs a target.", nameof(target));
            if (baseLayer is null)
                throw new ArgumentException("A map question needs a base layer.", nameof(baseLayer));

            // MapTarget's factories have already checked the shape and the ranges; the only thing
            // left is whether this particular country is on the map we ship.
            if (target.IsCountry && knownCountryCodes is not null && !knownCountryCodes.Contains(target.CountryCode!))
                throw new ArgumentException($"'{target.CountryCode}' is not a country the map can draw.", nameof(target));

            return;
        }

        if (kind == QuestionKind.Players)
        {
            // A players question's options are the match's own participants, filled in when it is
            // served — there is nothing here for choices or a correct index to mean, so both are
            // empty rather than "ignored", exactly as Map's are.
            if (choices.Count != 0)
                throw new ArgumentException("A players question has no fixed choices.", nameof(choices));
            if (correctIndex != 0)
                throw new ArgumentOutOfRangeException(nameof(correctIndex), correctIndex, "A players question has no correct choice; leave it at 0.");
            if (target is not null)
                throw new ArgumentException("A players question cannot have a map target.", nameof(target));
            if (baseLayer is not null)
                throw new ArgumentException("A players question cannot have a map base layer.", nameof(baseLayer));

            return;
        }

        // Choice and Sort share the shape of the list — four distinct, non-blank strings — and
        // differ only in what CorrectIndex means. A sort's answer is the order the items are stored
        // in, so there is no index to point at and it is pinned to 0.
        if (choices.Count != MatchRules.ChoicesPerQuestion)
            throw new ArgumentException($"A question needs exactly {MatchRules.ChoicesPerQuestion} choices, got {choices.Count}.", nameof(choices));
        if (choices.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Choices cannot be blank.", nameof(choices));
        if (choices.Select(c => c.Trim().ToLowerInvariant()).Distinct().Count() != choices.Count)
            throw new ArgumentException("Choices must be distinct.", nameof(choices));

        if (kind == QuestionKind.Sort)
        {
            if (correctIndex != 0)
                throw new ArgumentOutOfRangeException(nameof(correctIndex), correctIndex, "A sorting question's answer is the stored order; leave the index at 0.");
        }
        else if (correctIndex < 0 || correctIndex >= choices.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(correctIndex), "The correct answer must be one of the choices.");
        }

        if (target is not null)
            throw new ArgumentException($"A {kind} question cannot have a map target.", nameof(target));
        if (baseLayer is not null)
            throw new ArgumentException($"A {kind} question cannot have a map base layer.", nameof(baseLayer));
    }

    /// <summary>
    /// The choices exactly as a card must serve them for this question, in this round. Every card
    /// builder goes through here and none of them shuffles on its own.
    /// <para>
    /// There are three card builders — the async endpoint, the live view mapper and the live grain's
    /// own round push — and the spec is blunt about why they must not each reach for
    /// <see cref="SortOrder"/> themselves: if any one of them derived the order differently, a
    /// reconnect or a silo restart could show a player one arrangement and grade them against
    /// another, which reads to the player as the game marking a right answer wrong. One method, one
    /// permutation, and a builder cannot get it wrong by forgetting the kind: a
    /// <see cref="QuestionKind.Choice"/> question serves its options as stored, a
    /// <see cref="QuestionKind.Sort"/> one serves them shuffled, and a
    /// <see cref="QuestionKind.Map"/> one has none to serve.
    /// </para>
    /// <para>
    /// Note what this deliberately does not return: which stored index each served item came from.
    /// For a sort question the stored order <i>is</i> the answer, so handing a card the permutation
    /// alongside the items would put the answer on the wire under a different name.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> ServedChoices(string matchId, int slot) => Kind == QuestionKind.Sort
        ? SortOrder.For(matchId, slot, Choices.Count).Shuffle(Choices)
        : Choices;

    /// <summary>A <see cref="QuestionKind.Players"/> question has no correct choice — its point is
    /// comparing who picked whom, not grading anyone right or wrong — so no index is ever correct.</summary>
    public bool IsCorrect(int choiceIndex) => Kind != QuestionKind.Players && choiceIndex == CorrectIndex;

    /// <summary>
    /// Grades an answer that arrives as a string — a sorting order or a map answer. Wrong rather
    /// than thrown for anything malformed: grading runs inside a live round, and one player sending
    /// nonsense must not take the round down for everybody else.
    /// <para>
    /// A <see cref="QuestionKind.Sort"/> answer is checked against the identity order and needs no
    /// seed, because the submission path has already normalised the player's served positions into
    /// stored-index terms (see <see cref="SortOrder.ToStoredOrder"/>). "Right" is therefore literally
    /// "0,1,2,3", and nothing downstream that merely reads an answer can disagree with what was
    /// graded.
    /// </para>
    /// <para>
    /// <see cref="QuestionKind.Choice"/> is included for the caller that grades every kind through
    /// one door: its answer travels as <c>ChoiceIndex</c>, so the string is read as that index if it
    /// is one. It is not an invitation to stop calling <see cref="IsCorrect(int)"/>.
    /// </para>
    /// </summary>
    public bool IsCorrect(string? response) => Kind switch
    {
        QuestionKind.Sort => SortOrder.TryParseOrder(response, Choices.Count, out var order)
                             && SortOrder.IsIdentity(order),
        QuestionKind.Map => Target is not null && Target.Matches(response),
        _ => int.TryParse(response?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
             && IsCorrect(index)
    };

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
        IReadOnlyList<string> choices, int correctIndex, MediaRef? media, string? explanation,
        QuestionKind kind = QuestionKind.Choice, MapTarget? target = null, MapBaseLayer? baseLayer = null,
        IReadOnlySet<string>? knownCountryCodes = null)
    {
        Validate(prompt, choices, correctIndex, kind, target, baseLayer, knownCountryCodes);

        Lang = lang;
        CategoryId = categoryId;
        Level = level;
        Prompt = prompt.Trim();
        Choices = [.. choices.Select(c => c.Trim())];
        CorrectIndex = correctIndex;
        Media = media ?? MediaRef.None;
        Explanation = explanation;

        // The kind is edited with everything else, and the two map fields move with it. An edit that
        // changed the kind but left the old kind's fields behind is exactly the stray-target case the
        // validation table exists to prevent, so they are always written together.
        Kind = kind;
        Target = target;
        BaseLayer = baseLayer;
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
