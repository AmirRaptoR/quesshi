namespace Quesshi.Application.Ports;

public sealed record VotingSlotClosedPush(int Slot, IReadOnlyList<VotingAnswerPush> Answers);

public sealed record VotingAnswerPush(string PlayerId, int Kind, string? ParticipantId, int? ChoiceIndex);

/// <summary>Outbound voting events. Pushes are best-effort and never part of a state mutation.</summary>
public interface IVotingNotifier
{
    Task SlotClosedAsync(string matchId, VotingSlotClosedPush push, CancellationToken ct = default);
    Task RosterChangedAsync(string matchId, CancellationToken ct = default);
}
