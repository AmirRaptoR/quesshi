using Quesshi.Shared;

namespace Quesshi.Web.Tests;

public sealed class EmojiIconTests
{
    [Theory]
    [InlineData("🍳")]
    [InlineData("🇳🇱")]
    [InlineData("👨‍👩‍👧‍👦")]
    [InlineData("✈️")]
    [InlineData("👍🏽")]
    [InlineData("1️⃣")]
    [InlineData("#️⃣")]
    [InlineData("*️⃣")]
    public void One_complete_emoji_sequence_is_valid(string value)
        => Assert.True(EmojiIcon.TryNormalize(value, out var normalized) && normalized == value);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("A")]
    [InlineData("icon")]
    [InlineData("◆")]
    [InlineData("→")]
    [InlineData("⌘")]
    [InlineData("😀😃")]
    [InlineData("🏽")]
    [InlineData("️")]
    [InlineData("🇳")]
    [InlineData("👨‍")]
    public void Missing_non_emoji_multiple_or_malformed_values_are_invalid(string? value)
        => Assert.False(EmojiIcon.TryNormalize(value, out _));

    [Fact]
    public void Outer_whitespace_is_trimmed_without_normalizing_the_sequence()
    {
        Assert.True(EmojiIcon.TryNormalize("  👍🏽  ", out var normalized));
        Assert.Equal("👍🏽", normalized);
    }
}
