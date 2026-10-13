using System.Text.Json;
using Quesshi.Application.Sessions;
using Quesshi.Engine.Contracts;

namespace Quesshi.Application.Tests;

public sealed class SessionViewRendererTests
{
    [Fact]
    public void Renderer_gives_each_viewer_only_their_answer_until_the_session_ends()
    {
        var state = SessionState.Create(
            new SessionRow("session", null, null, new Position(0, 0), Version: 7), Json("{}"),
            [new Seat("alice", "player"), new Seat("bob", "player", ReleasedSlot: 0)],
            [SessionItemState.Create(new SessionItem(0, "kind", 1, null, null), Json("{}"), Json("{}"), Json("{\"card\":true}"), Json("{\"secret\":true}"))])
            with
        { Answers = new Dictionary<AnswerKey, AnswerRecord> { [new("bob", 0)] = new(Json("{\"secret\":\"bob-answer\"}"), false, 0, 0, TimeSpan.Zero, DateTimeOffset.UnixEpoch) } };

        var renderer = new ViewRenderer();
        var view = renderer.Render(state, new ProbeFormat(), "alice");
        var bobView = renderer.Render(state, new ProbeFormat(), "bob");

        Assert.Equal(7, view.Version);
        Assert.Equal(view.Version, bobView.Version);
        Assert.DoesNotContain("bob-answer", view.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", view.Json, StringComparison.Ordinal);
        Assert.Contains("card", view.Json, StringComparison.Ordinal);
        Assert.Contains("bob-answer", bobView.Json, StringComparison.Ordinal);
        Assert.Contains("secret", bobView.Json, StringComparison.Ordinal);
    }

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    private sealed class ProbeFormat : IFormat
    {
        public string Id => "probe";
        public FormatSpec Spec { get; } = new(Pacing.Shared, 1, 2, true, false, _ => true, true, null, false, [], TimeSpan.Zero, new Dictionary<string, Type>());
        public FormatVersions Versions { get; } = new(1, 1, 1);
        public JsonElement ReadRules(JsonElement raw) => raw;
        public DrawPlan Plan(ContentSpec content, JsonElement rules) => new(1, []);
        public void Start(ISession session) { }
        public void Apply(ISession session, Command command) { }
        public void Timeout(ISession session) { }
        public object View(IViewing viewing) => new { card = viewing.Card(0), own = viewing.Answer(viewing.Viewer, 0)?.Value, other = viewing.Answer("bob", 0)?.Value, reveal = viewing.Reveal(0) };
        public JsonElement UpgradeState(int from, JsonElement state) => state;
        public JsonElement UpgradeRules(int from, JsonElement rules) => rules;
        public JsonElement UpgradeResult(int from, JsonElement result) => result;
    }
}
