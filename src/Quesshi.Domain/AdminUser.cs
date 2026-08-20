namespace Quesshi.Domain;

/// <summary>
/// An administrator account. Deliberately a different entity from <see cref="Player"/>: playing the
/// game and administering it are separate identities, so a game session carries no privilege at all.
/// </summary>
public sealed class AdminUser
{
    public const int MaxFailedAttempts = 10;
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    private AdminUser(string id, string username, string email, string passwordHash, DateTimeOffset createdAt)
    {
        Id = id;
        Username = username;
        Email = email;
        PasswordHash = passwordHash;
        CreatedAt = createdAt;
    }

    public string Id { get; }
    public string Username { get; }
    public string Email { get; private set; }
    public string PasswordHash { get; private set; }
    public bool IsActive { get; private set; } = true;
    public bool MustChangePassword { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? LastLoginAt { get; private set; }
    public int FailedAttempts { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }

    public static AdminUser Create(string id, string username, string email, string passwordHash, DateTimeOffset now,
        bool mustChangePassword = false)
        => new(id, Fold(username), Fold(email), passwordHash, now) { MustChangePassword = mustChangePassword };

    public static AdminUser Restore(string id, string username, string email, string passwordHash, bool isActive,
        bool mustChangePassword, DateTimeOffset createdAt, DateTimeOffset? lastLoginAt, int failedAttempts, DateTimeOffset? lockedUntil)
        => new(id, username, email, passwordHash, createdAt)
        {
            IsActive = isActive,
            MustChangePassword = mustChangePassword,
            LastLoginAt = lastLoginAt,
            FailedAttempts = failedAttempts,
            LockedUntil = lockedUntil
        };

    public bool IsLocked(DateTimeOffset now) => LockedUntil is { } until && now < until;

    /// <summary>Throttling is the only thing standing between a password and an offline-speed guesser.</summary>
    public void RecordFailure(DateTimeOffset now)
    {
        FailedAttempts++;
        if (FailedAttempts >= MaxFailedAttempts) LockedUntil = now + LockDuration;
    }

    public void RecordSuccess(DateTimeOffset now)
    {
        FailedAttempts = 0;
        LockedUntil = null;
        LastLoginAt = now;
    }

    public void SetPassword(string passwordHash, DateTimeOffset now)
    {
        PasswordHash = passwordHash;
        MustChangePassword = false;
        FailedAttempts = 0;
        LockedUntil = null;
    }

    public void ChangeEmail(string email) => Email = Fold(email);

    public void Activate() => IsActive = true;
    public void Deactivate() => IsActive = false;

    private static string Fold(string value) => value.Trim().ToLowerInvariant();
}
