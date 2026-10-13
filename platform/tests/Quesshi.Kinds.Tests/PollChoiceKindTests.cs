using System.Text.Json;
using Quesshi.Kinds.PollChoice;
using Quesshi.Shared;

namespace Quesshi.Kinds.Tests;

public sealed class PollChoiceKindTests
{
    private readonly PollChoiceKind _kind = new();

    [Fact]
    public void Metadata_validation_and_card_follow_poll_choice_contract()
    {
        Assert.Equal(("poll_choice", 1, "poll", false), (_kind.Id, _kind.Version, _kind.Family, _kind.Gradable));
        var payload = new PollChoicePayload([new("green", " Green "), new("blue", "Blue")]);
        Assert.Empty(_kind.Validate(payload, new ValidateEnv("en")));
        Assert.Equal([new PollChoiceOption("green", "Green"), new PollChoiceOption("blue", "Blue")], _kind.Card(new(payload, new())).Options);
        Assert.Contains(_kind.Validate(new([new("one", "One")]), new ValidateEnv("en")), p => p.Code == "options.tooFew");
        Assert.Contains(_kind.Validate(new([new("1", "One"), new("2", "Two"), new("3", "Three"), new("4", "Four"), new("5", "Five"), new("6", "Six"), new("7", "Seven"), new("8", "Eight"), new("9", "Nine")]), new ValidateEnv("en")), p => p.Code == "options.tooMany");
        Assert.Contains(_kind.Validate(new([new(" ", "One"), new("two", " ")]), new ValidateEnv("en")), p => p.Code == "options.blank");
        Assert.Contains(_kind.Validate(new([new("same", "One"), new("same", "Two")]), new ValidateEnv("en")), p => p.Code == "options.duplicateId");
        Assert.Contains(_kind.Validate(new([new("one", " One "), new("two", "one")]), new ValidateEnv("en")), p => p.Code == "options.duplicateText");
    }

    [Fact]
    public void Normalizes_valid_answers_and_rejects_invalid_or_conflicting_shapes()
    {
        IKindRuntime runtime = _kind;
        var payload = Json("{\"options\":[{\"id\":\"green\",\"text\":\"Green\"},{\"id\":\"blue\",\"text\":\"Blue\"}]}");
        var resolved = runtime.Resolve(payload, new ResolveContext("session", 0, []));

        Assert.Equal("{\"option\":\"green\"}", runtime.Normalize(payload, resolved, Json("{\"option\":\"green\"}"))?.GetRawText());
        Assert.Equal("{\"notApplicable\":true}", runtime.Normalize(payload, resolved, Json("{\"notApplicable\":true}"))?.GetRawText());
        foreach (var answer in new[] { "{\"option\":\"unknown\"}", "{\"seat\":1}", "{\"option\":1}", "{}", "{\"option\":\"green\",\"notApplicable\":true}", "{\"notApplicable\":false}" })
            Assert.Throws<MalformedAnswer>(() => runtime.Normalize(payload, resolved, Json(answer)));
        Assert.Null(runtime.Normalize(payload, resolved, null));
        Assert.True(runtime.Grade(payload, resolved, Json("{\"option\":\"green\"}")).IsNone);
        Assert.True(runtime.Grade(payload, resolved, Json("{\"notApplicable\":true}")).IsNone);
        Assert.True(runtime.Grade(payload, resolved, null).IsNone);
    }

    [Fact]
    public void Snapshot_keeps_options_and_answers_after_source_option_changes()
    {
        IKindRuntime runtime = _kind;
        var originalPayload = Json("{\"options\":[{\"id\":\"a\",\"text\":\"A\"},{\"id\":\"b\",\"text\":\"B\"}]}");
        var changedPayload = Json("{\"options\":[{\"id\":\"new\",\"text\":\"New\"},{\"id\":\"other\",\"text\":\"Other\"}]}");
        var resolved = Json("{}");
        Assert.NotEqual(runtime.Card(originalPayload, resolved).GetRawText(), runtime.Card(changedPayload, resolved).GetRawText());
        var snapshot = Json("{\"card\":{\"options\":[{\"id\":\"a\",\"text\":\"A\"},{\"id\":\"b\",\"text\":\"B\"}]},\"reveal\":{},\"answers\":[{\"option\":\"b\"},{\"notApplicable\":true}]}");
        Assert.Equal(snapshot.GetRawText(), runtime.UpgradeSnapshot(1, snapshot).GetRawText());
    }

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
}
