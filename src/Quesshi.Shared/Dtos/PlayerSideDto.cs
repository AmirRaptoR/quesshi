namespace Quesshi.Shared;

public sealed record PlayerSideDto(string Id, string DisplayName, string AvatarSeed, int Score, int Correct, int Answered, bool Finished);
