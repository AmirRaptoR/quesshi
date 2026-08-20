using MongoDB.Bson.Serialization.Attributes;
using Quesshi.Domain;

namespace Quesshi.Infrastructure.Mongo;

/// <remarks>Extra elements are ignored so a removed field cannot break start-up.</remarks>
[BsonIgnoreExtraElements]
public sealed class AdminUserDoc
{
    [BsonId] public string Id { get; set; } = "";
    public string Username { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public int FailedAttempts { get; set; }
    public DateTime? LockedUntil { get; set; }

    public static AdminUserDoc From(AdminUser user) => new()
    {
        Id = user.Id,
        Username = user.Username,
        Email = user.Email,
        PasswordHash = user.PasswordHash,
        IsActive = user.IsActive,
        MustChangePassword = user.MustChangePassword,
        CreatedAt = user.CreatedAt.UtcDateTime,
        LastLoginAt = user.LastLoginAt?.UtcDateTime,
        FailedAttempts = user.FailedAttempts,
        LockedUntil = user.LockedUntil?.UtcDateTime
    };

    public AdminUser ToDomain() => AdminUser.Restore(Id, Username, Email, PasswordHash, IsActive, MustChangePassword,
        new DateTimeOffset(CreatedAt, TimeSpan.Zero),
        LastLoginAt is null ? null : new DateTimeOffset(LastLoginAt.Value, TimeSpan.Zero),
        FailedAttempts,
        LockedUntil is null ? null : new DateTimeOffset(LockedUntil.Value, TimeSpan.Zero));
}
