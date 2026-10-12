using System.Text.Json;
using Quesshi.Kinds.Choice;
using Quesshi.Shared;

namespace Quesshi.Kinds.Tests;

public sealed class ChoiceKindTests
{
    private readonly ChoiceKind _kind = new();

    [Fact]
    public void Metadata_and_validation_follow_choice_contract()
    {
        Assert.Equal(("choice", 1, "trivia", true), (_kind.Id, _kind.Version, _kind.Family, _kind.Gradable));
        Assert.Empty(_kind.Validate(Payload([" one ", "two", "three", "four"], 1), new ValidateEnv("en")));
        Assert.Contains(_kind.Validate(Payload(["one", "two", "three"], 0), new ValidateEnv("en")), x => x.Code == "options.tooFew");
        Assert.Contains(_kind.Validate(Payload(["one", "two", "three", "four", "five"], 0), new ValidateEnv("en")), x => x.Code == "options.tooMany");
        Assert.Contains(_kind.Validate(Payload(["one", " ", "three", "four"], 0), new ValidateEnv("en")), x => x.Code == "options.blank");
        Assert.Contains(_kind.Validate(Payload(["one", "ONE", "three", "four"], 0), new ValidateEnv("en")), x => x.Code == "options.duplicate");
        Assert.Contains(_kind.Validate(Payload(["one", "two", "three", "four"], 4), new ValidateEnv("en")), x => x.Code == "correctIndex.outOfRange");
    }

    [Fact]
    public void Card_is_author_order_without_answer_and_reveal_identifies_it()
    {
        var first = Item(Payload([" one ", "two", "three", "four"], 0));
        var second = Item(Payload([" one ", "two", "three", "four"], 3));

        Assert.Equal(new[] { "one", "two", "three", "four" }, _kind.Card(first).Options);
        Assert.Equal(_kind.Card(first).Options, _kind.Card(second).Options);
        Assert.Equal((0, "one"), (_kind.Reveal(first).CorrectIndex, _kind.Reveal(first).CorrectOption));
        Assert.Equal(new ChoiceResolved(), _kind.Resolve(first.Payload, new ResolveContext("any", 2, [])));
    }

    [Fact]
    public void Runtime_normalizes_and_grades_valid_answers_and_refuses_malformed_ones()
    {
        IKindRuntime runtime = _kind;
        var payload = Json("{\"options\":[\"one\",\"two\",\"three\",\"four\"],\"correctIndex\":2}");
        var resolved = runtime.Resolve(payload, new ResolveContext("session", 0, []));

        Assert.Equal("{\"index\":2}", runtime.Normalize(payload, resolved, Json("{\"index\":2}"))?.GetRawText());
        Assert.Equal(1, runtime.Grade(payload, resolved, Json("{\"index\":2}")).Credit);
        Assert.Equal(0, runtime.Grade(payload, resolved, Json("{\"index\":1}")).Credit);
        Assert.Null(runtime.Normalize(payload, resolved, null));
        Assert.Equal(0, runtime.Grade(payload, resolved, null).Credit);
        Assert.Throws<MalformedAnswer>(() => runtime.Normalize(payload, resolved, Json("{\"index\":4}")));
        Assert.Throws<MalformedAnswer>(() => runtime.Normalize(payload, resolved, Json("{\"index\":1.5}")));
    }

    private static ChoicePayload Payload(IReadOnlyList<string> options, int correctIndex) => new(options, correctIndex);
    private Item<ChoicePayload, ChoiceResolved> Item(ChoicePayload payload) => new(payload, _kind.Resolve(payload, new ResolveContext("session", 0, [])));
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
}
