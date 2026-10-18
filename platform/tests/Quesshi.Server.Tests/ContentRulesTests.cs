using Quesshi.Plugin.Api;

namespace Quesshi.Server.Tests;

public sealed class ContentRulesTests
{
    [Theory]
    [InlineData("What is the capital of France?", "what is the capital of france")]
    [InlineData("What is the capital of France?", "What  is the   capital of France!!")]
    [InlineData("How many strings does a guitar have?", "How many strings does a guitar have")]
    public void Wording_that_differs_only_in_case_or_punctuation_is_the_same_question(string first, string second)
    {
        Assert.True(PromptFingerprint.AreDuplicates(first, second));
    }

    [Theory]
    [InlineData("What is the capital of France?", "Which city is the capital of France?")]
    [InlineData("How many strings does a guitar have?", "A standard guitar has how many strings?")]
    [InlineData("پایتخت فرانسه كدام است؟", "پایتخت فرانسه کدام است")]
    [InlineData("بازی‌های المپیک هر چند سال است؟", "بازی های المپیک هر چند سال است؟")]
    public void The_same_question_asked_a_different_way_is_still_a_duplicate(string first, string second)
    {
        Assert.True(PromptFingerprint.AreDuplicates(first, second));
    }

    [Theory]
    [InlineData("What is the capital of France?", "What is the capital of Spain?")]
    [InlineData("Which planet is closest to the Sun?", "Which planet is largest in the Solar System?")]
    [InlineData("How many strings does a guitar have?", "How many strings does a violin have?")]
    public void Questions_about_different_things_are_not_duplicates(string first, string second)
    {
        Assert.False(PromptFingerprint.AreDuplicates(first, second));
    }

    [Fact]
    public void A_true_paraphrase_is_a_known_miss()
    {
        Assert.False(PromptFingerprint.AreDuplicates("Who wrote the novel Ulysses?", "The novel Ulysses was written by whom?"));
    }

    [Fact]
    public void Different_persian_questions_are_not_duplicates()
    {
        Assert.False(PromptFingerprint.AreDuplicates("پایتخت فرانسه کدام است؟", "پایتخت اسپانیا کدام است؟"));
    }

    [Fact]
    public void A_blank_prompt_never_matches_anything()
    {
        Assert.False(PromptFingerprint.AreDuplicates("", "What is the capital of France?"));
    }

    [Fact]
    public void An_existing_prompt_set_reports_the_first_thing_it_collides_with()
    {
        Assert.True(PromptFingerprint.CollidesWith("Which city is the capital of France?", ["What is the capital of France?", "Who painted the Mona Lisa?"]));
    }

    [Fact]
    public void A_batch_is_checked_against_everything_already_in_the_category()
    {
        var index = PromptIndex.FromPrompts(["What is the capital of France?", "Who painted the Mona Lisa?"]);

        Assert.True(index.Contains("Which city is the capital of France?"));
    }

    [Fact]
    public void Adding_as_we_go_stops_one_batch_repeating_itself()
    {
        var index = PromptIndex.FromPrompts([]);
        index.Add("What is the capital of France?");

        Assert.True(index.Contains("The capital of France is which city?"));
    }

    [Fact]
    public void Blank_prompts_are_not_indexed()
    {
        var index = PromptIndex.FromPrompts(["", "   "]);

        Assert.Equal(0, index.Count);
    }

    [Theory]
    [InlineData("The Atacama Desert lies mainly in which country?", "The Atacama Desert, one of the driest places on Earth, is located in which country?", "Chile")]
    [InlineData("«هفت سامورایی» ساخته کدام کارگردان است؟", "کدام کارگردان فیلم «هفت سامورایی» را ساخت که یکی از شاهکارهای سینمای ژاپن است؟", "آکیرا کوروساوا")]
    public void A_padded_rewording_with_the_same_answer_is_a_duplicate(string first, string second, string answer)
    {
        var index = new PromptIndex([(first, answer)]);

        Assert.True(index.Contains(second, answer));
    }

    [Fact]
    public void A_shared_answer_needs_real_evidence_beside_it()
    {
        var index = new PromptIndex([("First question?", "b")]);

        Assert.False(index.Contains("Second question?", "b"));
    }

    [Fact]
    public void Sharing_an_answer_is_not_enough_on_its_own()
    {
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

    [Fact]
    public void Topic_key_normalizes_a_valid_generated_topic()
    {
        Assert.Equal("solar system|largest planet", TopicKey.FromGenerated("Solar System", "Largest Planet"));
    }

    [Fact]
    public void Topic_key_rejects_a_repeated_generated_concept()
    {
        Assert.Null(TopicKey.FromGenerated("Largest Planet", "largest planet"));
    }
}
