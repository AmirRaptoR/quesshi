using System.Text.Json;
using Quesshi.Engine.Contracts;

namespace Quesshi.Bench.Live;

/// <summary>The deliberately small shared-pace format used only to measure the live path.</summary>
public sealed class BenchLiveFormat : IFormat
{
    public const int CountdownPhase = 0;
    public const int QuestionPhase = 1;
    public const int RevealPhase = 2;
    public static readonly TimeSpan Countdown = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan Question = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan Reveal = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(2);

    public string Id => "bench_live";
    public FormatSpec Spec { get; } = new(Pacing.Shared, 1, 500, Lobby: false, Scored: true,
        Accepts: kind => kind.Id == "choice", Guests: true, Matchmaking: null, Rematch: false,
        Roles: [new Role("player")], Grace: Grace, Actions: new Dictionary<string, Type>());
    public FormatVersions Versions { get; } = new(1, 1, 1);
    public TimeSpan? MaximumDeadlineLateness => null;
    public JsonElement ReadRules(JsonElement rules) => rules.ValueKind == JsonValueKind.Object ? rules.Clone() : throw new JsonException("Rules must be an object.");
    public DrawPlan Plan(ContentSpec content, JsonElement rules) => new(content.Count, ["choice"]);
    public void Start(ISession session) { session.SetPosition(CountdownPhase, 0); session.SetDeadline(session.Now + Countdown); }
    public void Apply(ISession session, Command command)
    {
        if (command.Type != CommandType.Answer || session.Session.Position is not { Phase: QuestionPhase } position) return;
        session.Record(session.Grade(command), 1);
        if (session.Seats.Count(seat => seat.Role == "player") == session.Seats.Count(seat => seat.Role == "player" && session.Answer(seat.Player, position.Slot) is not null)) Close(session, position.Slot);
    }
    public void Timeout(ISession session)
    {
        var position = session.Session.Position;
        if (position is null) return;
        if (position.Phase == CountdownPhase) { session.SetPosition(QuestionPhase, position.Slot); session.SetDeadline(session.Now + Question); return; }
        if (position.Phase == QuestionPhase)
        {
            var questionDeadline = position.Slot >= 0 && session.Session.PositionStartedAt is { } opened ? opened + Question : session.Now;
            if (session.Now < questionDeadline + Grace) { session.SetDeadline(questionDeadline + Grace); return; }
            Close(session, position.Slot); return;
        }
        if (position.Phase == RevealPhase)
        {
            if (position.Slot + 1 < session.ItemCount) { session.SetPosition(QuestionPhase, position.Slot + 1); session.SetDeadline(session.Now + Question); }
            else Finish(session);
        }
    }
    public object View(IViewing session)
    {
        var position = session.Session.Position;
        return new
        {
            phase = position?.Phase,
            slot = position?.Slot,
            ended = session.Session.Ended,
            card = position is { Slot: >= 0 } ? session.Card(position.Slot) : (JsonElement?)null,
        };
    }
    public DateTimeOffset? Deadline(IViewing session) => session.Session.Position is { Phase: QuestionPhase } && session.Session.PositionStartedAt is { } opened ? opened + Question : session.Session.DueAt;
    public JsonElement UpgradeRules(int from, JsonElement rules) => rules.Clone();
    public JsonElement UpgradeState(int from, JsonElement state) => state.Clone();
    public JsonElement UpgradeResult(int from, JsonElement result) => result.Clone();
    private static void Close(ISession session, int slot) { session.RecordMisses(slot); session.Release(slot); session.SetPosition(RevealPhase, slot); session.SetDeadline(session.Now + Reveal); }
    private static void Finish(ISession session) => session.End(EndReason.Completed, session.Seats.Where(x => x.Role == "player").Select((x, i) => new Standing(x.Player, i + 1, "draw")).ToArray(), JsonSerializer.SerializeToElement(new { completed = true }));
}
