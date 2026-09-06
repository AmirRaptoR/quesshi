namespace Quesshi.Domain;

public sealed class Player
{
    private readonly Dictionary<string, CategoryRecord> _byCategory = [];
    private readonly HashSet<string> _friends = [];
    private readonly List<DateTimeOffset> _abandonments = [];

    private Player(string id, string email, string displayName, Language lang, DateTimeOffset createdAt)
    {
        Id = id;
        Email = email;
        DisplayName = displayName;
        Lang = lang;
        CreatedAt = createdAt;
        AvatarSeed = id;
    }

    public string Id { get; }
    public string Email { get; }
    public string DisplayName { get; private set; }
    public string AvatarSeed { get; private set; }
    public Language Lang { get; private set; }
    public bool IsBanned { get; private set; }

    /// <summary>
    /// Someone who tapped an invite link and typed a name instead of signing in. They are a real
    /// player record because everything downstream — the match, the stats, the archive — is written
    /// in terms of one, but they own no address, so nothing can ever sign back in as them.
    /// </summary>
    public bool IsGuest { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public PlayerStats Stats { get; private set; } = PlayerStats.Empty;
    public IReadOnlyCollection<string> Friends => _friends;
    public IReadOnlyDictionary<string, CategoryRecord> ByCategory => _byCategory;

    /// <summary>Every abandonment still inside the rolling window, oldest first, as of the last time one was recorded.</summary>
    public IReadOnlyList<DateTimeOffset> Abandonments => _abandonments;

    public static Player Register(string id, string email, string displayName, Language lang, DateTimeOffset now)
        => new(id, email.Trim().ToLowerInvariant(), displayName.Trim(), lang, now);

    /// <summary>
    /// A one-duel player. The address is synthetic and deliberately unroutable — .invalid is
    /// reserved for exactly this by RFC 2606 — because the players collection holds a unique index
    /// on email and every guest still needs to occupy a distinct slot in it. No mail is ever sent
    /// there and no sign-in path accepts it.
    /// </summary>
    public static Player Guest(string id, string displayName, Language lang, DateTimeOffset now)
        => new(id, $"{id}@guest.invalid", displayName.Trim(), lang, now) { IsGuest = true };

    public void Rename(string displayName) => DisplayName = displayName.Trim();
    public void SetLanguage(Language lang) => Lang = lang;
    public void SetBanned(bool banned) => IsBanned = banned;

    public void RecordResult(MatchOutcome outcome, long score = 0)
    {
        var s = Stats;
        Stats = outcome switch
        {
            MatchOutcome.Win => s with { Wins = s.Wins + 1, Streak = s.Streak + 1, BestStreak = Math.Max(s.BestStreak, s.Streak + 1), TotalScore = Math.Max(0, s.TotalScore + score) },
            MatchOutcome.Loss => s with { Losses = s.Losses + 1, Streak = 0, TotalScore = Math.Max(0, s.TotalScore + score) },
            _ => s with { Draws = s.Draws + 1, TotalScore = Math.Max(0, s.TotalScore + score) }
        };
    }

    /// <summary>
    /// A live duel walked away from. Prunes anything outside <see cref="LiveRules.AbandonmentWindow"/>,
    /// counts this one in, and charges whatever that count costs straight out of the score already
    /// banked — floored at zero, same as every other place a score can move. Returns the penalty so
    /// the caller can apply the identical amount to the leaderboard.
    /// </summary>
    public int RecordAbandonment(DateTimeOffset now)
    {
        _abandonments.RemoveAll(at => now - at >= LiveRules.AbandonmentWindow);
        _abandonments.Add(now);

        var penalty = LiveRules.AbandonmentPenalty(_abandonments.Count);
        if (penalty > 0) Stats = Stats with { TotalScore = Math.Max(0, Stats.TotalScore - penalty) };
        return penalty;
    }

    public void RecordAnswer(string categoryId, bool correct)
    {
        var rec = _byCategory.GetValueOrDefault(categoryId, new CategoryRecord(0, 0));
        _byCategory[categoryId] = rec with { Asked = rec.Asked + 1, Correct = rec.Correct + (correct ? 1 : 0) };
    }

    public double Accuracy(string categoryId)
        => _byCategory.TryGetValue(categoryId, out var r) && r.Asked > 0 ? (double)r.Correct / r.Asked : 0.0;

    public void AddFriend(string playerId)
    {
        if (playerId != Id) _friends.Add(playerId);
    }

    public void RemoveFriend(string playerId) => _friends.Remove(playerId);

    public PlayerSnapshot ToSnapshot() => new(Id, Email, DisplayName, AvatarSeed, Lang, IsBanned, CreatedAt, Stats,
        new Dictionary<string, CategoryRecord>(_byCategory), [.. _friends], IsGuest, [.. _abandonments]);

    public static Player FromSnapshot(PlayerSnapshot s)
    {
        var p = new Player(s.Id, s.Email, s.DisplayName, s.Lang, s.CreatedAt)
        {
            AvatarSeed = s.AvatarSeed,
            IsBanned = s.IsBanned,
            IsGuest = s.IsGuest,
            Stats = s.Stats
        };
        foreach (var (k, v) in s.ByCategory) p._byCategory[k] = v;
        foreach (var f in s.Friends) p._friends.Add(f);
        if (s.Abandonments is not null) p._abandonments.AddRange(s.Abandonments);
        return p;
    }
}
