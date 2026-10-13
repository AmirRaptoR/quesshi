using System.Text.Json;
using Quesshi.Engine.Contracts;
using Quesshi.Shared;

namespace Quesshi.Formats.Testing;

/// <summary>A deliberately small shared-pacing format used to exercise the public format contract.</summary>
public sealed class EchoFormat : IFormat
{
    private static readonly IReadOnlyDictionary<string, Type> NoActions = new Dictionary<string, Type>();

    public string Id => "echo";
    public FormatSpec Spec { get; } = new(
        Pacing.Shared, 1, 8, Lobby: true, Scored: true, Accepts: _ => true, Guests: true,
        Matchmaking: null, Rematch: false, Roles: [new Role("player")], Grace: TimeSpan.Zero, Actions: NoActions);
    public FormatVersions Versions { get; } = new(1, 1, 1);

    public JsonElement ReadRules(JsonElement raw) => raw.ValueKind == JsonValueKind.Object
        ? raw.Clone()
        : throw new JsonException("Echo rules must be an object.");

    public DrawPlan Plan(ContentSpec content, JsonElement rules) => new(content.Count, ["echo"]);

    public void Start(ISession session)
    {
        session.State = Count(0);
        session.SetPosition(0, 0);
    }

    public void Apply(ISession session, Command command)
    {
        var graded = session.Grade(command);
        session.Record(graded, graded.IsGraded ? (int)Math.Round(graded.Credit * 100) : 0);
        var answered = ReadCount(session.State) + 1;
        var players = session.Seats.Count(seat => seat.Role == "player" && !seat.Eliminated);
        if (answered < players)
        {
            session.State = Count(answered);
            return;
        }

        var slot = command.At.Slot;
        if (slot + 1 < session.Items.Count)
        {
            session.State = Count(0);
            session.SetPosition(0, slot + 1);
            return;
        }

        var standings = session.Seats
            .Where(seat => seat.Role == "player")
            .OrderByDescending(seat => seat.Score)
            .ThenBy(seat => seat.Player, StringComparer.Ordinal)
            .Select((seat, index) => new Standing(seat.Player, index + 1))
            .ToArray();
        session.End(EndReason.Completed, standings, JsonSerializer.SerializeToElement(new { }));
    }

    public void Timeout(ISession session)
    {
        if (session.Session.Position is { } position) session.RecordMisses(position.Slot);
    }

    public object View(IViewing viewing) => new { viewing.Session, viewing.State, viewing.Seats, viewing.Items };
    public JsonElement UpgradeState(int from, JsonElement state) => Upgrade(from, state);
    public JsonElement UpgradeRules(int from, JsonElement rules) => Upgrade(from, rules);
    public JsonElement UpgradeResult(int from, JsonElement result) => Upgrade(from, result);

    private static JsonElement Upgrade(int from, JsonElement value) => from == 1
        ? value.Clone()
        : throw new ArgumentOutOfRangeException(nameof(from));

    private static JsonElement Count(int answered) => JsonSerializer.SerializeToElement(new EchoState(answered));
    private static int ReadCount(JsonElement state) => state.TryGetProperty("Answered", out var value) ? value.GetInt32() : 0;
    private sealed record EchoState(int Answered);
}
