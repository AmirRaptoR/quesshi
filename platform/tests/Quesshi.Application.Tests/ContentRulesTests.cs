using Quesshi.Application.Content;

namespace Quesshi.Application.Tests;

public sealed class ContentRulesTests
{
    [Theory]
    [InlineData("What is the capital of France?", "What  is the   capital of France!!")]
    [InlineData("How many strings does a guitar have?", "A standard guitar has how many strings?")]
    [InlineData("پایتخت فرانسه كدام است؟", "پایتخت فرانسه کدام است")]
    public void Prompt_fingerprint_recognizes_existing_duplicate_rules(string left, string right)
        => Assert.True(PromptFingerprint.AreDuplicates(left, right));

    [Theory]
    [InlineData("What is the capital of France?", "What is the capital of Spain?")]
    [InlineData("پایتخت فرانسه کدام است؟", "پایتخت اسپانیا کدام است؟")]
    public void Prompt_fingerprint_keeps_distinct_questions_distinct(string left, string right)
        => Assert.False(PromptFingerprint.AreDuplicates(left, right));

    [Fact]
    public void Prompt_index_rejects_a_duplicate_added_in_the_same_batch()
    {
        var index = PromptIndex.FromPrompts([]);
        index.Add("What is the capital of France?");

        Assert.True(index.Contains("The capital of France is which city?"));
    }

    [Theory]
    [InlineData("Inception", "director", "inception|director")]
    [InlineData("", "director", null)]
    public void Topic_key_normalizes_or_omits_missing_parts(string? subject, string? aspect, string? expected)
        => Assert.Equal(expected, TopicKey.From(subject, aspect));
}
