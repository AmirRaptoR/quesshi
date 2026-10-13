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

    [Fact]
    public void Snapshot_has_one_shared_serialization_and_personalizes_500_seated_views()
    {
        var state = State(500);
        var renderer = new ViewRenderer();
        var snapshot = renderer.Snapshot(state, DateTimeOffset.UnixEpoch);
        var format = new CountingFormat();

        var views = Enumerable.Range(0, 500)
            .Select(index => Assert.IsType<SessionView>(renderer.Render(snapshot, format, $"player-{index}")))
            .ToArray();

        Assert.Equal(500, format.Views);
        Assert.All(views, view => Assert.Contains("\"Version\":7", view.Json, StringComparison.Ordinal));
        using var first = JsonDocument.Parse(views[0].Json);
        using var second = JsonDocument.Parse(views[1].Json);
        Assert.Equal("player-0", first.RootElement.GetProperty("Body").GetProperty("viewer").GetString());
        Assert.Equal("player-1", second.RootElement.GetProperty("Body").GetProperty("viewer").GetString());
        Assert.Equal("session", first.RootElement.GetProperty("Id").GetString());
        Assert.Equal("code", first.RootElement.GetProperty("Code").GetString());
        Assert.Equal("probe", first.RootElement.GetProperty("Format").GetString());
        Assert.Equal("running", first.RootElement.GetProperty("Status").GetString());
        Assert.Equal(0, first.RootElement.GetProperty("Slot").GetInt32());
        Assert.Equal(7, first.RootElement.GetProperty("Version").GetInt64());
        Assert.Equal(3, first.RootElement.GetProperty("ConfigVersion").GetInt64());
        Assert.Equal(DateTimeOffset.UnixEpoch, first.RootElement.GetProperty("Now").GetDateTimeOffset());
        Assert.Equal("player-0", first.RootElement.GetProperty("Me").GetProperty("Id").GetString());
        Assert.Equal("test", first.RootElement.GetProperty("Content").GetProperty("source").GetString());
        Assert.Equal(1, first.RootElement.GetProperty("Rules").GetProperty("limit").GetInt32());
        Assert.Null(renderer.Render(snapshot, format, "unseated"));
    }

    [Theory]
    [InlineData(50, "Seats", 50)]
    [InlineData(51, "Top", 10)]
    public void Roster_is_complete_through_50_and_windowed_above_it(int count, string property, int expected)
    {
        var renderer = new ViewRenderer();
        var view = renderer.Render(renderer.Snapshot(State(count), DateTimeOffset.UnixEpoch), new ProbeFormat(), $"player-{count - 1}");
        using var json = JsonDocument.Parse(Assert.IsType<SessionView>(view).Json);

        Assert.Equal(expected, json.RootElement.GetProperty("Roster").GetProperty(property).GetArrayLength());
        if (count > 50)
        {
            Assert.Equal(51, json.RootElement.GetProperty("Roster").GetProperty("TotalSeats").GetInt32());
            Assert.Contains("player-50", json.RootElement.GetProperty("Roster").GetProperty("Neighbours").GetRawText(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Finished_large_roster_uses_the_standing_order_for_its_window()
    {
        var state = State(51, "finished") with
        {
            Seats = SessionState.ReadOnly(State(51).Seats.Values.Select(seat => seat.Player == "player-50"
                ? seat with { Place = 1 }
                : seat with { Place = seat.Number + 2 }).ToDictionary(seat => seat.Player)),
        };
        var renderer = new ViewRenderer();
        var view = renderer.Render(renderer.Snapshot(state, DateTimeOffset.UnixEpoch), new ProbeFormat(), "player-50");
        using var json = JsonDocument.Parse(Assert.IsType<SessionView>(view).Json);

        Assert.Equal("player-50", json.RootElement.GetProperty("Roster").GetProperty("Top")[0].GetProperty("Id").GetString());
        Assert.Contains("player-50", json.RootElement.GetProperty("Roster").GetProperty("Neighbours").GetRawText(), StringComparison.Ordinal);
    }

    private static SessionState State(int count, string status = "running") => SessionState.Create(
        new SessionRow("session", null, null, new Position(0, 0), Version: 7, Status: status, Format: "probe", Code: "code", Content: Json("{\"source\":\"test\"}"), Rules: Json("{\"limit\":1}"), ConfigVersion: 3),
        Json("{}"),
        Enumerable.Range(0, count).Select(index => new Seat($"player-{index}", "player", Score: index, Place: index + 1, Number: index, DisplayName: $"Player {index}")),
        [SessionItemState.Create(new SessionItem(0, "kind", 1, null, null), Json("{}"), Json("{}"), Json("{\"card\":true}"), Json("{\"secret\":true}"))]);

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    private class ProbeFormat : IFormat
    {
        public string Id => "probe";
        public FormatSpec Spec { get; } = new(Pacing.Shared, 1, 2, true, false, _ => true, true, null, false, [], TimeSpan.Zero, new Dictionary<string, Type>());
        public FormatVersions Versions { get; } = new(1, 1, 1);
        public JsonElement ReadRules(JsonElement raw) => raw;
        public DrawPlan Plan(ContentSpec content, JsonElement rules) => new(1, []);
        public void Start(ISession session) { }
        public void Apply(ISession session, Command command) { }
        public void Timeout(ISession session) { }
        public virtual object View(IViewing viewing) => new { card = viewing.Card(0), own = viewing.Answer(viewing.Viewer, 0)?.Value, other = viewing.Answer("bob", 0)?.Value, reveal = viewing.Reveal(0) };
        public JsonElement UpgradeState(int from, JsonElement state) => state;
        public JsonElement UpgradeRules(int from, JsonElement rules) => rules;
        public JsonElement UpgradeResult(int from, JsonElement result) => result;
    }

    private sealed class CountingFormat : ProbeFormat
    {
        public int Views { get; private set; }
        public override object View(IViewing viewing)
        {
            Views++;
            return new { viewer = viewing.Viewer, answer = viewing.Answer(viewing.Viewer, 0)?.Value };
        }
    }
}
