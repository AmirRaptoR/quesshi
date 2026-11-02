using System.Text.Json.Nodes;
using Quesshi.Shared;
using Quesshi.Web.Services;

namespace Quesshi.Web.Tests;

/// <summary>Builds running live <see cref="SessionView"/>s the way the server writes them, through the client's own parser.</summary>
internal static class LiveScreenViews
{
    public const int Countdown = 0;
    public const int Question = 1;
    public const int Reveal = 2;

    public sealed record Player(string Id, string Status = "active", int Score = 0);

    public static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static SessionView Parse(string me, Player[] players, int phase, int slot = 0, JsonObject? body = null, int total = 0,
        long version = 1, int? rosterTotal = null, string? meStatus = null)
    {
        var seats = new JsonArray(players.Select((player, index) => (JsonNode)new JsonObject
        {
            ["id"] = player.Id, ["name"] = player.Id.ToUpperInvariant(), ["seat"] = index, ["role"] = "player", ["status"] = player.Status,
            ["score"] = player.Score, ["place"] = null, ["outcome"] = null, ["online"] = true,
        }).ToArray());
        var mine = players.Single(player => player.Id == me);
        var roster = rosterTotal is { } big && big > players.Length
            ? new JsonObject { ["totalSeats"] = big, ["top"] = seats.DeepClone(), ["neighbours"] = new JsonArray() }
            : new JsonObject { ["total"] = players.Length, ["seats"] = seats };
        var view = new JsonObject
        {
            ["id"] = LobbyViews.SessionId, ["code"] = "ABCD23", ["format"] = "live", ["status"] = "running",
            ["phase"] = phase.ToString(), ["slot"] = slot, ["step"] = 0, ["version"] = version, ["configVersion"] = 1,
            ["now"] = Now.ToString("O"), ["deadline"] = Now.AddSeconds(20).ToString("O"),
            ["me"] = new JsonObject
            {
                ["id"] = me, ["seat"] = 0, ["role"] = "player", ["status"] = meStatus ?? mine.Status, ["score"] = mine.Score,
                ["place"] = null, ["outcome"] = null,
            },
            ["roster"] = roster,
            ["content"] = new JsonObject { ["count"] = total },
            ["rules"] = new JsonObject(),
            ["body"] = body ?? new JsonObject(),
        };
        return PlatformState.ParseView(view.ToJsonString())!;
    }

    public static JsonObject ChoiceBody(string[] answered, string[]? expected = null, bool reveal = false, string? mine = null,
        string[]? responded = null, string[]? previousResponded = null, int slot = 0)
    {
        var body = new JsonObject
        {
            ["Expected"] = new JsonArray((expected ?? ["amir", "sara"]).Select(id => (JsonNode)id).ToArray()),
            ["Answered"] = new JsonArray(answered.Select(id => (JsonNode)id).ToArray()),
            ["Kind"] = "choice",
            ["KindVersion"] = 1,
            ["Card"] = new JsonObject { ["options"] = new JsonArray("Tehran", "Paris") },
        };
        if (reveal) body["Reveal"] = new JsonObject { ["correctIndex"] = 0, ["correctOption"] = "Tehran" };
        if (mine is not null) body["Mine"] = JsonNode.Parse(mine);
        if (responded is not null)
            body["Current"] = new JsonObject { ["Slot"] = slot, ["Responded"] = new JsonArray(responded.Select(id => (JsonNode)id).ToArray()) };
        if (previousResponded is not null)
            body["Previous"] = new JsonObject { ["Slot"] = slot - 1, ["Responded"] = new JsonArray(previousResponded.Select(id => (JsonNode)id).ToArray()) };
        return body;
    }
}
