namespace Quesshi.Shared;

public sealed record PlayerSideDto(string Id, string DisplayName, string AvatarSeed,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? Score,
    int Correct, int Answered, bool Finished);
