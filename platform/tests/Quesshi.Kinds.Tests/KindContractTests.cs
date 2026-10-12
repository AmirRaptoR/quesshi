using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Quesshi.Shared;

namespace Quesshi.Kinds.Tests;

public sealed class KindContractTests
{
    private static readonly IKindRuntime Runtime = new TestKind();
    private static readonly JsonElement Payload = Json("{\"answer\":2}");
    private static readonly JsonElement Resolved = Json("{\"seed\":7}");

    [Fact]
    public void Typed_contract_and_json_runtime_exercise_every_operation()
    {
        var kind = new TestKind();
        var item = new Item<TestPayload, TestResolved>(new(2), new(7));

        Assert.Equal(2, kind.Upgrade(1, Payload).Answer);
        Assert.Equal(7, kind.UpgradeSnapshot(1, new RawSnapshot(Json("{\"value\":7}"), Json("{\"answer\":2}"), [Json("{\"value\":2}")])).Card.Value);
        Assert.Empty(kind.Validate(item.Payload, new ValidateEnv("en")));
        Assert.Equal(7, kind.Resolve(item.Payload, new ResolveContext("session", 0, [])).Seed);
        Assert.Equal(7, kind.Card(item).Value);
        Assert.Equal(2, kind.Reveal(item).Answer);
        Assert.Equal(2, kind.Normalize(item, new TestAnswer(2)).Value);
        Assert.Equal(1, kind.Grade(item, new TestStored(2)).Credit);

        Assert.Equal("{\"answer\":2}", Runtime.Upgrade(1, Payload).GetRawText());
        Assert.Equal("{\"card\":{\"value\":7},\"reveal\":{\"answer\":2},\"answers\":[{\"value\":2}]}",
            Runtime.UpgradeSnapshot(1, Json("{\"card\":{\"value\":7},\"reveal\":{\"answer\":2},\"answers\":[{\"value\":2}]}")).GetRawText());
        Assert.Empty(Runtime.Validate(Payload, new ValidateEnv("en")));
        Assert.Equal("{\"seed\":7}", Runtime.Resolve(Payload, new ResolveContext("session", 0, [])).GetRawText());
        Assert.Equal("{\"value\":7}", Runtime.Card(Payload, Resolved).GetRawText());
        Assert.Equal("{\"answer\":2}", Runtime.Reveal(Payload, Resolved).GetRawText());
        Assert.Equal("{\"value\":2}", Runtime.Normalize(Payload, Resolved, Json("{\"value\":2}"))?.GetRawText());
        Assert.Equal(1, Runtime.Grade(Payload, Resolved, Json("{\"value\":2}")).Credit);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    public void Runtime_rejects_non_object_values(string value)
    {
        var json = Json(value);
        Assert.Throws<JsonException>(() => Runtime.Validate(json, new ValidateEnv("en")));
        Assert.Throws<JsonException>(() => Runtime.Resolve(json, new ResolveContext("s", 0, [])));
        Assert.Throws<JsonException>(() => Runtime.Card(Payload, json));
        Assert.Throws<JsonException>(() => Runtime.Reveal(Payload, json));
    }

    [Fact]
    public void Runtime_rejects_non_object_outputs()
    {
        IKindRuntime runtime = new NonObjectTestKind();
        var payload = Json("{\"value\":2}");
        var resolved = Json("{\"value\":7}");

        Assert.Throws<JsonException>(() => runtime.Upgrade(1, payload));
        Assert.Throws<JsonException>(() => runtime.Resolve(payload, new ResolveContext("session", 0, [])));
        Assert.Throws<JsonException>(() => runtime.Card(payload, resolved));
        Assert.Throws<JsonException>(() => runtime.Reveal(payload, resolved));
    }

    [Fact]
    public void Grade_none_is_distinct_from_zero_and_invalid_credit_is_rejected()
    {
        var none = new TestKind { ReturnNone = true };
        var zero = new TestKind();
        Assert.True(((IKindRuntime)none).Grade(Payload, Resolved, Json("{\"value\":0}")).IsNone);
        Assert.False(((IKindRuntime)zero).Grade(Payload, Resolved, Json("{\"value\":0}")).IsNone);
        Assert.Equal(0, ((IKindRuntime)zero).Grade(Payload, Resolved, Json("{\"value\":0}")).Credit);
        Assert.True(((IKindRuntime)new TestKind { GradableValue = false }).Grade(Payload, Resolved, null).IsNone);

        foreach (var credit in new[] { 1.5, double.NaN, double.PositiveInfinity })
            Assert.Throws<InvalidOperationException>(() => ((IKindRuntime)new TestKind { Credit = credit }).Grade(Payload, Resolved, Json("{\"value\":0}")));
    }

    [Fact]
    public void Registry_keeps_versions_and_only_returns_explicit_current_version()
    {
        var v1 = new TestKind { VersionValue = 1 };
        var v2 = new TestKind { VersionValue = 2 };
        var registry = new KindRegistry([]);
        registry.Register(v1, current: true);
        registry.Register(v2, current: false);

        Assert.Same(v1, registry.GetCurrent("test"));
        Assert.Same(v2, registry.Get("test", 2));
        Assert.False(registry.TryGet("test", 3, out _));
        Assert.False(registry.TryGetCurrent("unknown", out _));
        Assert.Throws<KeyNotFoundException>(() => registry.Get("test", 3));
    }

    [Theory]
    [InlineData("alpha", 0, 1320619409127077649UL, 10475257336574687358UL, 15723740891041973097UL)]
    [InlineData("beta", 2, 17705641226850878542UL, 13135601502811879212UL, 11493578522956426082UL)]
    [InlineData("session-42", 7, 6281373536103499904UL, 4957097237242193170UL, 10943104128414948879UL)]
    public void Splitmix64_sequence_is_stable_for_session_and_slot(string sessionId, int slot, ulong expectedFirst, ulong expectedSecond, ulong expectedThird)
    {
        var first = DeterministicRng.For(sessionId, slot);
        var second = DeterministicRng.For(sessionId, slot);
        Assert.Equal([expectedFirst, expectedSecond, expectedThird],
            [first.NextUInt64(), first.NextUInt64(), first.NextUInt64()]);
        Assert.Equal([expectedFirst, expectedSecond, expectedThird],
            [second.NextUInt64(), second.NextUInt64(), second.NextUInt64()]);
        Assert.NotEqual(DeterministicRng.For(sessionId, slot).NextUInt64(), DeterministicRng.For(sessionId, slot + 1).NextUInt64());
    }

    [Fact]
    public void Splitmix64_sequence_is_stable_in_a_separate_process()
    {
        const string probeVariable = "QUESSHI_RNG_PROCESS_PROBE";
        if (Environment.GetEnvironmentVariable(probeVariable) == "1")
        {
            var rng = DeterministicRng.For("alpha", 0);
            Assert.Equal(1320619409127077649UL, rng.NextUInt64());
            Assert.Equal(10475257336574687358UL, rng.NextUInt64());
            return;
        }

        using var process = Process.Start(new ProcessStartInfo("dotnet")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            ArgumentList =
            {
                "vstest",
                typeof(KindContractTests).Assembly.Location,
                "--TestCaseFilter:FullyQualifiedName=Quesshi.Kinds.Tests.KindContractTests.Splitmix64_sequence_is_stable_in_a_separate_process"
            },
            Environment = { [probeVariable] = "1" }
        })!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{process.StandardOutput.ReadToEnd()}{process.StandardError.ReadToEnd()}");
    }

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
}

internal sealed class TestKind : Kind<TestPayload, TestResolved, TestCard, TestAnswer, TestStored, TestReveal>
{
    public override string Id => "test";
    public override int Version => VersionValue;
    public int VersionValue { get; init; } = 1;
    public override string Family => "test";
    public bool GradableValue { get; init; } = true;
    public override bool Gradable => GradableValue;
    public double Credit { get; init; }
    public bool ReturnNone { get; init; }
    protected override JsonSerializerContext SerializerContext => TestJsonContext.Default;
    public override TestPayload Upgrade(int from, JsonElement payload) => JsonSerializer.Deserialize(payload, TestJsonContext.Default.TestPayload)!;
    public override Snapshot<TestCard, TestReveal, TestStored> UpgradeSnapshot(int from, RawSnapshot old) =>
        new(JsonSerializer.Deserialize(old.Card, TestJsonContext.Default.TestCard)!, JsonSerializer.Deserialize(old.Reveal, TestJsonContext.Default.TestReveal)!, old.Answers.Select(answer => JsonSerializer.Deserialize(answer, TestJsonContext.Default.TestStored)!).ToArray());
    public override IReadOnlyList<Problem> Validate(TestPayload payload, ValidateEnv env) => [];
    public override TestResolved Resolve(TestPayload payload, ResolveContext context) => new(7);
    public override TestCard Card(Item<TestPayload, TestResolved> item) => new(item.Resolved.Seed);
    public override TestReveal Reveal(Item<TestPayload, TestResolved> item) => new(item.Payload.Answer);
    public override TestStored Normalize(Item<TestPayload, TestResolved> item, TestAnswer answer) => new(answer.Value);
    public override Grade Grade(Item<TestPayload, TestResolved> item, TestStored stored) => ReturnNone ? Quesshi.Shared.Grade.None : Quesshi.Shared.Grade.Of(Credit == 0 ? (stored.Value == item.Payload.Answer ? 1 : 0) : Credit);
}

internal sealed record TestPayload(int Answer);
internal sealed record TestResolved(int Seed);
internal sealed record TestCard(int Value);
internal sealed record TestAnswer(int Value);
internal sealed record TestStored(int Value);
internal sealed record TestReveal(int Answer);

[JsonConverter(typeof(ScalarJsonConverter))]
internal sealed record Scalar(int Value);

internal sealed class ScalarJsonConverter : JsonConverter<Scalar>
{
    public override Scalar Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number) return new(reader.GetInt32());
        using var document = JsonDocument.ParseValue(ref reader);
        return new(document.RootElement.GetProperty("value").GetInt32());
    }

    public override void Write(Utf8JsonWriter writer, Scalar value, JsonSerializerOptions options) => writer.WriteNumberValue(value.Value);
}

internal sealed class NonObjectTestKind : Kind<Scalar, Scalar, Scalar, TestAnswer, TestStored, Scalar>
{
    public override string Id => "non-object";
    public override int Version => 1;
    public override string Family => "test";
    public override bool Gradable => true;
    protected override JsonSerializerContext SerializerContext => NonObjectJsonContext.Default;
    public override Scalar Upgrade(int from, JsonElement payload) => JsonSerializer.Deserialize(payload, NonObjectJsonContext.Default.Scalar)!;
    public override Snapshot<Scalar, Scalar, TestStored> UpgradeSnapshot(int from, RawSnapshot old) =>
        new(JsonSerializer.Deserialize(old.Card, NonObjectJsonContext.Default.Scalar)!, JsonSerializer.Deserialize(old.Reveal, NonObjectJsonContext.Default.Scalar)!, old.Answers.Select(answer => JsonSerializer.Deserialize(answer, NonObjectJsonContext.Default.TestStored)!).ToArray());
    public override IReadOnlyList<Problem> Validate(Scalar payload, ValidateEnv env) => [];
    public override Scalar Resolve(Scalar payload, ResolveContext context) => new(7);
    public override Scalar Card(Item<Scalar, Scalar> item) => new(item.Resolved.Value);
    public override Scalar Reveal(Item<Scalar, Scalar> item) => new(item.Payload.Value);
    public override TestStored Normalize(Item<Scalar, Scalar> item, TestAnswer answer) => new(answer.Value);
    public override Grade Grade(Item<Scalar, Scalar> item, TestStored stored) => Quesshi.Shared.Grade.Of(0);
}

[JsonSerializable(typeof(TestPayload))]
[JsonSerializable(typeof(TestResolved))]
[JsonSerializable(typeof(TestCard))]
[JsonSerializable(typeof(TestAnswer))]
[JsonSerializable(typeof(TestStored))]
[JsonSerializable(typeof(TestReveal))]
[JsonSerializable(typeof(Snapshot<TestCard, TestReveal, TestStored>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class TestJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(Scalar))]
[JsonSerializable(typeof(TestAnswer))]
[JsonSerializable(typeof(TestStored))]
[JsonSerializable(typeof(Snapshot<Scalar, Scalar, TestStored>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class NonObjectJsonContext : JsonSerializerContext;
