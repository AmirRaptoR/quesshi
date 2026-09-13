namespace Quesshi.Application.Ports;

public sealed record MatchingSlotClosedPush(int Slot, IReadOnlyList<MatchingAnswerPush> Answers);

public sealed record MatchingAnswerPush(string PlayerId, int Kind, string? ParticipantId, int? ChoiceIndex);

/// <summary>Outbound matching events. Pushes are best-effort and never part of a state mutation.</summary>
public interface IMatchingNotifier
{
    Task SlotClosedAsync(string matchId, MatchingSlotClosedPush push, CancellationToken ct = default);
    Task RosterChangedAsync(string matchId, CancellationToken ct = default);
}
