using System.Text.Json;
using Quesshi.Shared;

namespace Quesshi.Kinds.Tests;

internal static class KindFixtureRunner
{
    public static void Run(string kind, IKindRuntime runtime, Func<JsonElement, JsonElement> changeCorrectAnswer, bool verifyExpected = true)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", kind);
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").OrderBy(path => path, StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var fixture = document.RootElement;
            var payload = runtime.Upgrade(runtime.Version, Required(fixture, "payload"));
            Assert.Empty(runtime.Validate(payload, new ValidateEnv("en")));
            var context = Context(Required(fixture, "seed"));
            var resolved = runtime.Resolve(payload, context);
            if (verifyExpected) AssertJson(Required(fixture, "resolved"), resolved, $"{path} resolved");
            var card = runtime.Card(payload, resolved);
            if (verifyExpected)
            {
                AssertJson(Required(fixture, "card"), card, $"{path} card");
                AssertJson(Required(fixture, "reveal"), runtime.Reveal(payload, resolved), $"{path} reveal");
            }

            var changedPayload = changeCorrectAnswer(payload);
            var changedResolved = runtime.Resolve(changedPayload, Context(Required(fixture, "seed")));
            if (!JsonElement.DeepEquals(card, runtime.Card(changedPayload, changedResolved)))
                throw new InvalidOperationException($"{runtime.Id} v{runtime.Version} card depends on its correct answer.");

            if (verifyExpected)
                foreach (var answerCase in Required(fixture, "answers").EnumerateArray())
                    CheckAnswer(runtime, payload, resolved, answerCase, path);
        }
    }

    private static void CheckAnswer(IKindRuntime runtime, JsonElement payload, JsonElement resolved, JsonElement answerCase, string path)
    {
        JsonElement? submitted = answerCase.TryGetProperty("submitted", out var answer) ? answer : null;
        if (answerCase.TryGetProperty("malformed", out var malformed) && malformed.GetBoolean())
        {
            Assert.Throws<MalformedAnswer>(() => runtime.Normalize(payload, resolved, submitted));
            return;
        }

        var stored = runtime.Normalize(payload, resolved, submitted);
        if (answerCase.TryGetProperty("stored", out var expectedStored))
        {
            Assert.NotNull(stored);
            AssertJson(expectedStored, stored!.Value, $"{path} stored answer");
        }
        else Assert.Null(stored);

        Assert.Equal(answerCase.GetProperty("credit").GetDouble(), runtime.Grade(payload, resolved, stored).Credit);
    }

    private static ResolveContext Context(JsonElement seed) => new(seed.GetProperty("sessionId").GetString()!, seed.GetProperty("slot").GetInt32(), []);
    private static JsonElement Required(JsonElement source, string property) => source.TryGetProperty(property, out var value)
        ? value
        : throw new InvalidDataException($"Fixture is missing '{property}'.");
    private static void AssertJson(JsonElement expected, JsonElement actual, string message) =>
        Assert.True(JsonElement.DeepEquals(expected, actual), $"{message}: expected {expected.GetRawText()}, actual {actual.GetRawText()}");
}
