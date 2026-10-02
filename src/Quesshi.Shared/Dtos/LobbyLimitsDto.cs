namespace Quesshi.Shared;

public sealed record LobbyLimitsDto(int MaxCapacity)
{
    public const int MinCapacity = 2;
    public const int DefaultMaxCapacity = 20;
    public const int AbsoluteMaxCapacity = 500;
    public const int VotingMaxCapacity = 8;
}
