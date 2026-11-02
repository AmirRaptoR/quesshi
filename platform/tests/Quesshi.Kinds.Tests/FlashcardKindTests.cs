using System.Text.Json;
using Quesshi.Kinds.Flashcard;
using Quesshi.Shared;

namespace Quesshi.Kinds.Tests;

public sealed class FlashcardKindTests
{
    private readonly FlashcardKind _kind = new();
    private static readonly JsonElement Payload = Json("{\"back\":\" Paris \"}");

    [Fact]
    public void Metadata_follows_the_flashcard_contract() =>
        Assert.Equal(("flashcard", 1, "card", false), (_kind.Id, _kind.Version, _kind.Family, _kind.Gradable));

    [Fact]
    public void Card_carries_nothing_and_never_the_back_text()
    {
        IKindRuntime runtime = _kind;
        var card = runtime.Card(Payload, runtime.Resolve(Payload, new ResolveContext("session", 0, []))).GetRawText();
        Assert.Equal("{}", card);
        Assert.DoesNotContain("Paris", card);
    }

    [Fact]
    public void Reveal_carries_the_back_text()
    {
        IKindRuntime runtime = _kind;
        Assert.Equal("{\"back\":\"Paris\"}", runtime.Reveal(Payload, Json("{}")).GetRawText());
    }

    [Fact]
    public void Validation_rejects_a_blank_back()
    {
        Assert.Empty(_kind.Validate(new("Paris"), new ValidateEnv("en")));
        Assert.Contains(_kind.Validate(new(" "), new ValidateEnv("en")), p => p.Code == "back.blank");
        Assert.Contains(_kind.Validate(new(null), new ValidateEnv("en")), p => p.Code == "back.blank");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Accepts_ratings_zero_to_three(int rating)
    {
        IKindRuntime runtime = _kind;
        Assert.Equal($"{{\"rating\":{rating}}}", runtime.Normalize(Payload, Json("{}"), Json($"{{\"rating\":{rating}}}"))?.GetRawText());
        Assert.True(runtime.Grade(Payload, Json("{}"), Json($"{{\"rating\":{rating}}}")).IsNone);
    }

    [Theory]
    [InlineData("{\"rating\":-1}")]
    [InlineData("{\"rating\":4}")]
    [InlineData("{\"rating\":1.5}")]
    [InlineData("{\"rating\":\"2\"}")]
    [InlineData("{\"rating\":true}")]
    [InlineData("{\"rating\":null}")]
    [InlineData("{}")]
    public void Rejects_any_other_rating(string answer)
    {
        IKindRuntime runtime = _kind;
        Assert.Throws<MalformedAnswer>(() => runtime.Normalize(Payload, Json("{}"), Json(answer)));
    }

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
}
