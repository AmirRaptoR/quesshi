using Quesshi.Domain;

namespace Quesshi.Infrastructure.Generation;

/// <summary>
/// Builds the instructions sent to the model. Separate from the HTTP client so the wording can be
/// tuned without touching transport, and so swapping providers does not rewrite the prompt.
/// </summary>
public sealed class QuestionPromptBuilder
{
    public string System() =>
        "You write multiple-choice trivia questions for a two-player quiz game. " +
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
        - The wrong choices must be plausible and of similar length to the right one — no giveaways.
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
