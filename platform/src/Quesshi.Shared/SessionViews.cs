using System.Text.Json;

namespace Quesshi.Shared;

/// <summary>Wire contract shared by command responses and the future session stream.</summary>
public sealed record SessionView(
    string Id,
    string Code,
    string Format,
    string Status,
    string Phase,
    int Slot,
    int Step,
    long Version,
    long ConfigVersion,
    DateTimeOffset Now,
    DateTimeOffset? Deadline,
    MySeat Me,
    RosterView Roster,
    JsonElement Content,
    JsonElement Rules,
    JsonElement Body);

public sealed record MySeat(string Id, int Seat, string Role, string Status, int Score, int? Place, string? Outcome);
public sealed record SessionRosterSeat(string Id, string Name, int Seat, string Role, string Status, int Score, int? Place, string? Outcome,
    bool Online = false);

/// <summary>A complete roster is sent only while it remains small enough for every view.</summary>
public abstract record RosterView(int Total);
public sealed record CompleteRosterView(IReadOnlyList<SessionRosterSeat> Seats) : RosterView(Seats.Count);
public sealed record WindowedRosterView(int TotalSeats, IReadOnlyList<SessionRosterSeat> Top, IReadOnlyList<SessionRosterSeat> Neighbours) : RosterView(TotalSeats);

/// <summary>A stable, cursor-paged roster response for sessions whose view is windowed.</summary>
public sealed record RosterPage(IReadOnlyList<SessionRosterSeat> Seats, int? NextSeat);
