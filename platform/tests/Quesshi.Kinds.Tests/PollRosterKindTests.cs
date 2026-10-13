using System.Text.Json;
using Quesshi.Kinds.PollRoster;
using Quesshi.Shared;

namespace Quesshi.Kinds.Tests;

public sealed class PollRosterKindTests
{
    private readonly PollRosterKind _kind = new();

    [Fact]
    public void Metadata_and_resolve_freeze_active_players_in_roster_order()
    {
        Assert.Equal(("poll_roster", 1, "poll", false), (_kind.Id, _kind.Version, _kind.Family, _kind.Gradable));
        var payload = new PollRosterPayload();
        var resolved = _kind.Resolve(payload, Context((1, "player", "active", "Ada"), (2, "spectator", "active", "Bea"), (3, "player", "left", "Cy"), (4, "player", "active", "Dee"), (5, "player", "active", "Eve"), (6, "player", "active", "Fox"), (7, "player", "active", "Gia")));
        Assert.Empty(_kind.Validate(payload, new ValidateEnv("en")));
        Assert.Equal([new PollRosterOption(1, "Ada"), new(4, "Dee"), new(5, "Eve"), new(6, "Fox"), new(7, "Gia")], resolved.Options);
        Assert.Equal(resolved.Options, _kind.Card(new(payload, resolved)).Options);
        Assert.Equal(new PollRosterReveal(), _kind.Reveal(new(payload, resolved)));
    }

    [Fact]
    public void Normalizes_every_answer_form_and_rejects_wrong_or_conflicting_shapes()
    {
        IKindRuntime runtime = _kind;
        var payload = Json("{}");
        var resolved = runtime.Resolve(payload, Context((4, "player", "active", "Dee")));
        foreach (var pair in new[] { ("{\"seat\":4}", "{\"seat\":4}"), ("{\"several\":true}", "{\"several\":true}"), ("{\"none\":true}", "{\"none\":true}"), ("{\"notApplicable\":true}", "{\"notApplicable\":true}") })
        {
            var stored = runtime.Normalize(payload, resolved, Json(pair.Item1));
            Assert.Equal(pair.Item2, stored?.GetRawText());
            Assert.True(runtime.Grade(payload, resolved, stored).IsNone);
        }
        foreach (var answer in new[] { "{\"seat\":9}", "{\"option\":\"green\"}", "{\"seat\":\"4\"}", "{}", "{\"seat\":4,\"none\":true}", "{\"several\":false}" })
            Assert.Throws<MalformedAnswer>(() => runtime.Normalize(payload, resolved, Json(answer)));
        Assert.True(runtime.Grade(payload, resolved, null).IsNone);
    }

    [Fact]
    public void Snapshot_keeps_frozen_names_seats_and_answer_set_after_roster_changes()
    {
        IKindRuntime runtime = _kind;
        var payload = Json("{}");
        var resolved = runtime.Resolve(payload, Context((4, "player", "active", "Dee")));
        var originalCard = runtime.Card(payload, resolved);
        var changed = runtime.Resolve(payload, Context((4, "player", "left", "Renamed"), (8, "player", "active", "New")));
        Assert.Equal("{\"options\":[{\"seat\":4,\"name\":\"Dee\"}]}", originalCard.GetRawText());
        Assert.Throws<MalformedAnswer>(() => runtime.Normalize(payload, resolved, Json("{\"seat\":8}")));
        Assert.NotEqual(originalCard, runtime.Card(payload, changed));
        var snapshot = Json("{\"card\":{\"options\":[{\"seat\":4,\"name\":\"Dee\"}]},\"reveal\":{},\"answers\":[{\"seat\":4},{\"several\":true},{\"none\":true},{\"notApplicable\":true}]}");
        Assert.Equal(snapshot.GetRawText(), runtime.UpgradeSnapshot(1, snapshot).GetRawText());
    }

    private static ResolveContext Context(params (int Seat, string Role, string Status, string Name)[] seats) =>
        new("session", 0, seats.Select(seat => new RosterSeat(seat.Seat, seat.Role, seat.Status, seat.Name)).ToArray());
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();
}
