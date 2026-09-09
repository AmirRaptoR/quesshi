using Quesshi.Domain;

namespace Quesshi.Infrastructure.Generation;

/// <summary>
/// Builds the instructions sent to the model. Separate from the HTTP client so the wording can be
/// tuned without touching transport, and so swapping providers does not rewrite the prompt.
/// </summary>
public sealed class QuestionPromptBuilder
{
    /// <summary>
    /// The one system message every kind shares. Deliberately says "questions", not "multiple-choice
    /// questions", now that a batch may be sorting or map questions — the shape is the user message's
    /// business and the schema's, and a system prompt that contradicted them would be one more thing
    /// for a model to weigh.
    /// </summary>
    public string System() =>
        "You write trivia questions for a two-player quiz game. " +
        "You answer only with JSON matching the requested schema, and never with commentary.";

    public string User(Language lang, Category category, Difficulty level, int count, IReadOnlyCollection<string> avoid)
    {
        var language = Name(lang);
        var audience = lang switch
        {
            Language.Fa => "Persian-speaking players. Prefer facts a Persian speaker finds interesting, including Iranian and regional subjects, without making every question about Iran.",
            Language.Nl => "Dutch-speaking players and people learning Dutch. Prefer plain, current Dutch and subjects that come up in life in the Netherlands.",
            _ => "an international English-speaking audience."
        };

        return $"""
        Write {count} multiple-choice trivia questions.

        Category: {category.NameEn}
        Language: write the prompt and every choice in {language}, for {audience}
        Difficulty: {Describe(level)}

        Rules:
        - Exactly {MatchRules.ChoicesPerQuestion} choices per question, exactly one of them correct.
        - The wrong choices must be plausible and within a few characters of the right one in length,
          and written in the same grammatical shape. A careful definition against three short
          dismissals gives the answer away to anyone who cannot read the language at all.
        - Every question must have a single, checkable, uncontested answer.
        - No questions about current events, ages, or anything that changes over time.
        - Keep the prompt under 120 characters and each choice under 40.
        - Vary which position holds the correct answer.
        - Add a one-sentence explanation, in the same language, saying why the answer is right.

        Also give each question a "subject" and an "aspect". These two identify the question, so
        that two differently worded questions about the same fact come out identical:
        - "subject" is the specific thing the question is about, in English, as a proper name where
          one exists: "Inception", "Tweede Kamer", "Rotterdam". Never a category or a broad theme —
          "film", "politics", "transport" and "culture" are all too vague to identify anything.
        - "aspect" is the single property being asked, in English, one or two words: "director",
          "capital", "founding year", "emergency number".
        - Two questions in this batch must never share the same subject and aspect.

        Examples: "Wie regisseerde Inception?" is inception / director. "Hoeveel zetels heeft de
        Tweede Kamer?" is tweede kamer / seat count. "Wat is gezelligheid?" is gezelligheid /
        meaning — the subject is the word itself, not "culture".{Avoid(avoid)}
        """;
    }

    /// <summary>
    /// Questions where the picture is the question. The subject must be the correct answer, so the
    /// image is sourced from the answer rather than the answer being trusted to match the image.
    /// </summary>
    public string Illustrated(Language lang, Category category, Difficulty level, int count, IReadOnlyCollection<string> avoid)
    {
        var language = Name(lang);

        return $"""
        Write {count} multiple-choice questions that are answered by looking at a photograph.

        Category: {category.NameEn}
        Language: write the prompt and every choice in {language}
        Difficulty: {Describe(level)}

        Each question carries a "subject": the exact title of the English Wikipedia article for the
        CORRECT answer. We fetch that article's photograph and show it to the player, so:

        - The subject must be a concrete thing with an obvious photograph: an animal, plant, food,
          building, landmark, instrument, vehicle, mineral, flag, or a famous painting.
        - The subject MUST be the correct answer, never a hint or a piece of context.
        - Phrase the prompt about the picture: "Which animal is this?", "What is this building?".
          Never put a name in the prompt that gives the answer away.
        - The wrong choices must be things that look plausibly similar in a photograph.
        - Avoid living people, film stills, album covers and logos: those pictures are not free to use.
        - Prefer subjects whose Wikipedia article certainly has a lead photograph.

        Rules:
        - Exactly {MatchRules.ChoicesPerQuestion} choices, exactly one correct.
        - Keep the prompt under 90 characters and each choice under 40.
        - Vary which position holds the correct answer.
        - Add a one-sentence explanation in the same language.{Avoid(avoid)}
        """;
    }

    /// <summary>
    /// Sorting questions: four items and the axis that orders them.
    ///
    /// <para>
    /// The hard rule here is that the criterion must be objective and measurable, and must be
    /// visible to the player in the prompt itself. "Order these by population" is checkable by
    /// anybody; "order these by beauty" stores an opinion and then tells every player who disagrees
    /// that they are wrong, with a report path as the only remedy.
    /// </para>
    /// <para>
    /// <b>This is prompt guidance, not validation, and the difference matters.</b> Nothing
    /// downstream can decide mechanically whether an axis is objective — "order these by
    /// importance" is a grammatical, schema-valid, well-formed question and no check will ever say
    /// otherwise. A subjective sort therefore passes validation and reaches players. The defence is
    /// human review, which is the concrete reason <c>docs/sorting-and-map-questions.md</c> asks for
    /// the first top-up of this kind to be run with <c>Generation:AutoApprove</c> off, plus the
    /// player report path afterwards. Saying so here rather than in a commit message, because the
    /// next person to read this file will be looking for the validation that does not exist.
    /// </para>
    /// </summary>
    public string Sort(Language lang, Category category, Difficulty level, int count, IReadOnlyCollection<string> avoid)
    {
        var language = Name(lang);

        return $"""
        Write {count} ordering questions. Each one gives {MatchRules.ChoicesPerQuestion} items that the
        player has to put in the right order.

        Category: {category.NameEn}
        Language: write the prompt and every item in {language}
        Difficulty: {Describe(level)}

        The criterion is everything:
        - Order by something MEASURABLE and objective: population, year, height, length, area,
          distance, duration, temperature, price, date of founding, number of something.
        - NEVER order by taste, beauty, importance, fame, difficulty, quality or influence. Those
          are opinions, and a player who disagrees is simply told they are wrong.
        - State the criterion and its direction in the prompt the player reads: "Order these cities
          by population, largest first", "Put these treaties in order, oldest first". A player who
          cannot see what they are ordering by is guessing.
        - The four values must be far enough apart that nobody could argue the order, and none of
          them may be tied. Avoid anything that changes month to month.

        Rules:
        - Exactly {MatchRules.ChoicesPerQuestion} items, given IN THE CORRECT ORDER, first to last.
          The game shuffles them before a player sees them, so the order you write is the answer.
        - Items are short names, under 40 characters, in the same grammatical shape as each other.
        - Keep the prompt under 120 characters.
        - Add a one-sentence explanation, in the same language, giving the actual values that decide
          the order — that is what a reviewer checks the question against.

        Also give each question a "subject" and an "aspect", in English, which together identify it:
        - "subject" is what is being ordered, as a proper name or a specific class: "European
          capitals", "Studio Ghibli films", "Iranian provinces". Never "geography" or "history".
        - "aspect" is the criterion itself, one or two words: "population", "founding year",
          "elevation". Two questions in this batch must never share a subject and aspect.{Avoid(avoid)}
        """;
    }

    /// <summary>
    /// Map questions: find a country, or find a city within a tolerance radius.
    ///
    /// <para>
    /// The city case asks for one thing it does not store — the country the city is in. That is
    /// deliberate and it is the whole defence for this kind. A model will name a real city, name its
    /// real country and then give coordinates in a different one entirely; the strings all read
    /// perfectly, and the only witness is the map. So the pipeline asks for both halves and checks
    /// them against the actual outlines of the bundled SVG before storing anything
    /// (<c>WorldMapGeometry</c>). The country is thrown away afterwards — it was never part of the
    /// question, only of the proof.
    /// </para>
    /// <para>
    /// The model is told that the check exists, and told what happens when it fails. A model that
    /// knows its coordinates will be verified tends to reach for the ones it is sure of.
    /// </para>
    /// </summary>
    public string Map(Language lang, Category category, Difficulty level, int count, IReadOnlyCollection<string> avoid)
    {
        var language = Name(lang);

        return $"""
        Write {count} questions answered by pointing at a place on a world map.

        Category: {category.NameEn}
        Language: write the prompt in {language}
        Difficulty: {Describe(level)}

        Each question is one of two shapes, and you say which:
        - "country": the answer is a whole country. Give its ISO 3166-1 alpha-2 code in
          "countryCode" — "DE", "IR", "NL". Leave the coordinates and the radius null.
        - "city": the answer is a point. Give its latitude and longitude in decimal degrees, the
          ISO 3166-1 alpha-2 code of THE COUNTRY THAT CITY IS IN, and how many kilometres from the
          point still counts as right.

        About those coordinates, because this is where these questions go wrong:
        - They are checked against the country you name, on the same map the player taps. If the
          point does not fall inside that country, the question is thrown away and nobody sees it.
        - So give coordinates you are sure of, for a city you are sure of, in the country you name.
          A famous city with coordinates you half-remember is worth less than a smaller one you know.
        - Latitude is north-positive and runs -90 to 90; longitude is east-positive and runs -180 to
          180. Tehran is about 35.7, 51.4. Rio de Janeiro is about -22.9, -43.2.

        The tolerance radius is the difficulty lever: 50 km is hard, 150 km is normal, 300 km is
        gentle. It must be between {MapTarget.MinRadiusKm} and {MapTarget.MaxRadiusKm} kilometres.

        Rules:
        - The prompt must NOT contain the answer's name, or there is nothing to find. Ask for it:
          "Which country is this flag from?" is not a map question; "Where is the Strait of Hormuz?"
          is. A prompt may describe the place, name what happened there, or name what it is famous
          for.
        - Prefer places a player can actually hit on a world map with no zoom: a small island nation
          is a fair country question only at the hardest levels.
        - Keep the prompt under 120 characters.
        - Add a one-sentence explanation, in the same language, naming the place — the prompt hides
          the answer, the explanation gives it back once the round is over.

        Also give each question a "subject" and an "aspect", in English, which together identify it:
        - "subject" is the place itself: "Reykjavik", "Strait of Hormuz", "Portugal".
        - "aspect" is what is being asked about it, one or two words: "location", "capital".
        - Two questions in this batch must never share a subject and aspect.{Avoid(avoid)}
        """;
    }

    private static string Describe(Difficulty level) => level switch
    {
        Difficulty.VeryEasy => "Very easy — almost everybody knows this. A child or a complete newcomer should get it.",
        Difficulty.Easy => "Easy — most adults get it right without thinking hard.",
        Difficulty.Medium => "Medium — a well-read player gets it, a casual one might not.",
        Difficulty.Hard => "Hard — most players will not know it, but an enthusiast of this topic will.",
        _ => "Very hard — for someone who really knows the subject. Still a fair, checkable fact, never obscure trivia nobody could reason about."
    };

    /// <summary>A nudge only: the real de-duplication happens on the way back, in TopUpQuestionBank.</summary>
    private static string Avoid(IReadOnlyCollection<string> avoid)
        => avoid.Count == 0
            ? ""
            : $"\n\nDo NOT repeat or paraphrase any of these existing questions:\n- {string.Join("\n- ", avoid)}";

    private static string Name(Language lang) => lang switch
    {
        Language.Fa => "Persian (Farsi)",
        Language.Nl => "Dutch (Nederlands)",
        _ => "English"
    };
}
