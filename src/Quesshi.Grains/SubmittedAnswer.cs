using Quesshi.Domain;

namespace Quesshi.Grains;

/// <summary>
/// What a player sent, turned into what gets stored and whether it is right. The one place either
/// duel type does that, and — for a sorting question — the one place in the whole system where the
/// served shuffle is inverted.
/// <para>
/// It lives at the grain layer rather than in the domain because grading a sort needs the seed, and
/// the seed is <c>(matchId, slot)</c>: reconstructible only where the match is known. Both grains
/// already computed correctness here for a choice question, and both hold the match id and the slot,
/// so this is where the new kinds join rather than a new place invented for them.
/// </para>
/// <para>
/// Normalising here, once, is the decision the rest of the feature rests on. What reaches
/// <c>AnswerRecord</c>/<c>LiveAnswer</c> is the answer in <b>stored-index</b> terms, so correctness
/// is simply "is this the identity order", <see cref="Question.IsCorrect(string?)"/> needs no seed,
/// and no reveal or history mapper ever needs one either — nothing that merely <i>reads</i> a stored
/// answer can disagree with what was graded.
/// </para>
/// </summary>
internal static class SubmittedAnswer
{
    /// <summary>
    /// The choice index a sorting or map answer carries: there is no choice to point at, so it
    /// reuses the sentinel a timeout already used. That collides only on paper — a timed-out answer
    /// of any kind has a null <see cref="GradedAnswer.Response"/>, a played sort or map answer has
    /// one — which is why every caller here tells the two apart by the response and never by the
    /// index alone.
    /// </summary>
    private const int NoChoice = -1;

    /// <summary>A miss: nothing was played, nothing is right. The shape a timed-out answer of any
    /// kind is stored in.</summary>
    private static readonly GradedAnswer TimedOut = new(NoChoice, null, false);

    /// <summary>
    /// Grades <paramref name="response"/> (or <paramref name="choiceIndex"/>, for a
    /// <see cref="QuestionKind.Choice"/> question) against <paramref name="question"/>.
    /// <para>
    /// False means the submission was malformed and must be refused outright — not stored as a wrong
    /// answer. That distinction is deliberate: a sorting answer that is not a permutation of the
    /// items, or a map answer that does not parse, is not a player being wrong, it is a client
    /// sending something no interface can produce, and storing it would put a value into the run that
    /// every later reader has to defend itself against. Refusal is the same shape a bad choice index
    /// already gets.
    /// </para>
    /// <para>
    /// A missing response for a sorting or map question is <i>not</i> malformed: that is the timeout,
    /// which an async run submits explicitly so it can move on. A live duel has no such submission —
    /// its buzzer records the miss itself, in <c>LiveMatch.CloseRound</c> — so <c>LiveMatchGrain</c>
    /// refuses an empty response before it ever gets here.
    /// </para>
    /// </summary>
    internal static bool TryGrade(Question question, string matchId, int slot, int choiceIndex, string? response,
        out GradedAnswer graded)
    {
        graded = TimedOut;

        switch (question.Kind)
        {
            case QuestionKind.Sort:
            {
                if (string.IsNullOrWhiteSpace(response)) return true; // the clock ran out

                // The wire carries served positions in the order the player placed them: "2,0,3,1"
                // means "the item you showed me third goes first". TryParseOrder is what makes that a
                // trust boundary — invariant culture, so Persian digits from a Persian browser are
                // rejected rather than misread, and a permutation check, so a repeated or
                // out-of-range index cannot get through and quietly grade as an answer.
                if (!SortOrder.TryParseOrder(response, question.Choices.Count, out var servedPositions))
                    return false;

                // The single inversion. Everything downstream sees stored indices and needs no seed.
                var stored = SortOrder.FormatOrder(
                    SortOrder.For(matchId, slot, question.Choices.Count).ToStoredOrder(servedPositions));

                graded = new GradedAnswer(NoChoice, stored, question.IsCorrect(stored));
                return true;
            }

            case QuestionKind.Map:
            {
                if (string.IsNullOrWhiteSpace(response)) return true; // the clock ran out
                if (!TryNormaliseMapAnswer(question.Target, response, out var normalised)) return false;

                graded = new GradedAnswer(NoChoice, normalised, question.IsCorrect(normalised));
                return true;
            }

            default:
                // Byte-identical to what a choice answer always did, including the >= 0 that is the
                // timeout check — it just no longer stands between the other two kinds and their own
                // grading. Any response string a client attached to a choice answer is dropped rather
                // than stored: the choice index is the answer, and a second field claiming to be one
                // could only ever contradict it.
                graded = new GradedAnswer(choiceIndex, null, choiceIndex >= 0 && question.IsCorrect(choiceIndex));
                return true;
        }
    }

    /// <summary>
    /// A map answer as it will be stored: an upper-cased country code, or a point in
    /// <see cref="Geo.Format(double, double)"/>'s canonical form.
    /// <para>
    /// Normalised rather than kept verbatim so that history renders one spelling of an answer instead
    /// of whatever the client's culture happened to emit, and refused rather than coerced when it
    /// does not fit the target's shape. <see cref="Geo.TryParse"/> is the load-bearing part: it reads
    /// invariant culture only, so a Persian or German decimal comma in <c>"52,37"</c> fails to parse
    /// instead of becoming a thousands-separated 5237 somewhere in the Indian Ocean, and Persian
    /// digits are rejected outright. This app runs in Persian and its client takes the browser's
    /// culture, so that is a real submission, not a hypothetical one.
    /// </para>
    /// </summary>
    private static bool TryNormaliseMapAnswer(MapTarget? target, string response, out string normalised)
    {
        normalised = string.Empty;

        // A map question with no target cannot be graded at all. Validation refuses to author one, so
        // this is unreachable short of hand-edited storage; refusing beats grading everybody wrong.
        if (target is null) return false;

        if (target.IsCountry)
        {
            // The shape of a code, not whether it is the right one — a wrong country is a wrong
            // answer, and must be recorded as one. Anything that is not an ISO 3166-1 alpha-2 code at
            // all is a client fault, not a player's guess.
            var code = response.Trim().ToUpperInvariant();
            if (code.Length != 2 || !code.All(char.IsAsciiLetterUpper)) return false;

            normalised = code;
            return true;
        }

        if (!Geo.TryParse(response, out var latitude, out var longitude)) return false;

        normalised = Geo.Format(latitude, longitude);
        return true;
    }
}

/// <summary>
/// One graded submission, in the shape the domain stores it: the choice index (-1 for anything that
/// is not a <see cref="QuestionKind.Choice"/> answer), the normalised response (null for a choice
/// answer and for a timeout of any kind), and whether it was right.
/// </summary>
internal readonly record struct GradedAnswer(int ChoiceIndex, string? Response, bool Correct);
