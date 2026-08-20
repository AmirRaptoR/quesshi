namespace Quesshi.Shared;

public sealed record AdminIdentityDto(string Id, string Username, string Email, bool MustChangePassword);
