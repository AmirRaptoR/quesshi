using Quesshi.Domain;

namespace Quesshi.Domain.Tests;

public class PromptFingerprintTests
{
    [Theory]
    [InlineData("What is the capital of France?", "what is the capital of france")]
    [InlineData("What is the capital of France?", "What  is the   capital of France!!")]
    [InlineData("How many strings does a guitar have?", "How many strings does a guitar have")]
    public void Wording_that_differs_only_in_case_or_punctuation_is_the_same_question(string a, string b)
        => Assert.True(PromptFingerprint.AreDuplicates(a, b));

    [Theory]
    [InlineData("What is the capital of France?", "Which city is the capital of France?")]
    [InlineData("How many strings does a guitar have?", "A standard guitar has how many strings?")]
    public void The_same_question_asked_a_different_way_is_still_a_duplicate(string a, string b)
        => Assert.True(PromptFingerprint.AreDuplicates(a, b));

    [Fact]
    public void A_true_paraphrase_is_a_known_miss()
    {
        // Documented ceiling, not an accident: matching "wrote" to "written by" needs meaning,
        // not tokens. See the note on PromptFingerprint.
        Assert.False(PromptFingerprint.AreDuplicates("Who wrote the novel Ulysses?", "The novel Ulysses was written by whom?"));
    }

    [Theory]
    [InlineData("What is the capital of France?", "What is the capital of Spain?")]
    [InlineData("Which planet is closest to the Sun?", "Which planet is largest in the Solar System?")]
    [InlineData("How many strings does a guitar have?", "How many strings does a violin have?")]
    public void Questions_about_different_things_are_not_duplicates(string a, string b)
        => Assert.False(PromptFingerprint.AreDuplicates(a, b));

    [Theory]
    // Persian is written with two different yeh and kaf codepoints, and a zero-width non-joiner.
    [InlineData("پایتخت فرانسه كدام است؟", "پایتخت فرانسه کدام است")]
    [InlineData("بازی‌های المپیک هر چند سال است؟", "بازی های المپیک هر چند سال است؟")]
    public void Persian_spelling_variants_are_the_same_question(string a, string b)
        => Assert.True(PromptFingerprint.AreDuplicates(a, b));

    [Fact]
    public void Different_persian_questions_are_not_duplicates()
        => Assert.False(PromptFingerprint.AreDuplicates("پایتخت فرانسه کدام است؟", "پایتخت اسپانیا کدام است؟"));

    [Fact]
    public void A_blank_prompt_never_matches_anything()
    {
        Assert.False(PromptFingerprint.AreDuplicates("", "What is the capital of France?"));
        Assert.False(PromptFingerprint.AreDuplicates("   ", "  "));
    }

    [Fact]
    public void An_existing_prompt_set_reports_the_first_thing_it_collides_with()
    {
        string[] existing = ["What is the capital of France?", "Who painted the Mona Lisa?"];

        Assert.True(PromptFingerprint.CollidesWith("Which city is the capital of France?", existing));
        Assert.False(PromptFingerprint.CollidesWith("What is the capital of Peru?", existing));
    }
}

public class PromptIndexTests
{
    [Fact]
    public void A_batch_is_checked_against_everything_already_in_the_category()
    {
        var index = PromptIndex.FromPrompts(["What is the capital of France?", "Who painted the Mona Lisa?"]);

        Assert.True(index.Contains("Which city is the capital of France?"));
        Assert.False(index.Contains("What is the capital of Peru?"));
    }

    [Fact]
    public void Adding_as_we_go_stops_one_batch_repeating_itself()
    {
        var index = PromptIndex.FromPrompts([]);
        index.Add("What is the capital of France?");

        Assert.True(index.Contains("The capital of France is which city?"));
        Assert.Equal(1, index.Count);
    }

    [Fact]
    public void Blank_prompts_are_not_indexed()
    {
        var index = PromptIndex.FromPrompts(["", "   "]);
        Assert.Equal(0, index.Count);
    }
}

public class PaddedDuplicateTests
{
    // Both pairs are real duplicates found in the live bank. Padding a question with extra words
    // dilutes the shared-word ratio below the plain threshold, so the answer has to count too.
    [Theory]
    [InlineData("The Atacama Desert lies mainly in which country?",
                "The Atacama Desert, one of the driest places on Earth, is located in which country?", "Chile")]
    [InlineData("«هفت سامورایی» ساخته کدام کارگردان است؟",
                "کدام کارگردان فیلم «هفت سامورایی» را ساخت که یکی از شاهکارهای سینمای ژاپن است؟", "آکیرا کوروساوا")]
    public void A_padded_rewording_with_the_same_answer_is_a_duplicate(string a, string b, string answer)
    {
        var index = new PromptIndex([(a, answer)]);
        Assert.True(index.Contains(b, answer));
    }

    [Fact]
    public void A_shared_answer_needs_real_evidence_beside_it()
    {
        // One word in common is not evidence, however matching the answers are.
        var index = new PromptIndex([("First question?", "b")]);
        Assert.False(index.Contains("Second question?", "b"));
    }

    [Fact]
    public void Sharing_an_answer_is_not_enough_on_its_own()
    {
        // Both answer "five", and both are perfectly good separate questions.
        var index = new PromptIndex([("How many rings are on the Olympic flag?", "Five")]);
        Assert.False(index.Contains("How many points is a try worth in rugby union?", "Five"));
    }

    [Fact]
    public void A_different_answer_still_needs_the_strict_overlap()
    {
        var index = new PromptIndex([("What is the capital of France?", "Paris")]);
        Assert.False(index.Contains("What is the capital of Spain?", "Madrid"));
    }

    [Fact]
    public void A_reworded_question_with_a_different_answer_is_not_a_duplicate()
    {
        var index = new PromptIndex([("Which country has the largest land area?", "Russia")]);
        Assert.False(index.Contains("Which country has the largest population?", "India"));
    }
}
