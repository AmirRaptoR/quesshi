namespace Quesshi.Web.Models;

public sealed record PlayerProfile(Guid Id, string DisplayName, string AvatarSeed, string Lang, bool IsGuest = false);

public sealed record VerificationResult(string Status, string? Token, string? DisplayName, string? AvatarSeed, string? Lang);
