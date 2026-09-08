namespace Quesshi.Domain;

public sealed class Player
{
    private readonly Dictionary<string, CategoryRecord> _byCategory = [];
    private readonly HashSet<string> _friends = [];
    private readonly List<DateTimeOffset> _abandonments = [];
    private readonly List<string> _settledMatchIds = [];

    /// <summary>
    /// How many settled match ids one player document remembers for dedup. A duel that somehow
    /// settles later than this many subsequent duels for the same player could double-apply — far
    /// outside any real retry or recovery window — and the alternative is an unbounded list on what
    /// is otherwise a small, hot document.
    /// </summary>
    public const int MaxSettledMatchIds = 200;

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

    /// <summary>The most recent <see cref="MaxSettledMatchIds"/> match ids this player has been settled for, oldest first.</summary>
    public IReadOnlyList<string> SettledMatchIds => _settledMatchIds;

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
            MatchOutcome.Win => s with { Wins = s.Wins + 1, Streak = s.Streak + 1, BestStreak = Math.Max(s.BestStreak, s.Streak + 1), TotalScore = s.TotalScore + score },
            MatchOutcome.Loss => s with { Losses = s.Losses + 1, Streak = 0, TotalScore = s.TotalScore + score },
            _ => s with { Draws = s.Draws + 1, TotalScore = s.TotalScore + score }
        };
    }

    /// <summary>
    /// A live duel walked away from. <paramref name="at"/> is an event time — the match's own
    /// <c>EndedAt</c> — not wall-clock, and it need not arrive in order: a settlement that fails and
    /// retries, or a recovery days later, can settle a duel that ended after one that ended earlier.
    /// So <paramref name="at"/> is inserted into <see cref="_abandonments"/> in timestamp order, and
    /// the tier is computed from whatever is on record inside the window around <paramref name="at"/>
    /// itself — <c>(at - AbandonmentWindow, at]</c> — rather than around whenever this happens to run.
    /// An abandonment whose timestamp is after <paramref name="at"/> is excluded by that upper bound
    /// regardless of when it was inserted, which is what keeps a late-processed but early-dated
    /// abandonment from being charged for one that, from its own point in time, had not happened yet.
    /// The penalty is charged straight out of the score already banked, floored at zero same as every
    /// other place a score can move. Returns the penalty so the caller can mirror it onto the
    /// leaderboard.
    /// </summary>
    /// <remarks>
    /// This can only ever undercharge relative to a fully order-aware reconciliation, never
    /// overcharge: if a later-dated duel settles first, its tier is computed without an earlier duel
    /// that has not been inserted yet, and inserting that earlier duel afterwards does not retroactively
    /// raise a tier already applied. Making that exact would mean storing every event's applied penalty
    /// and reconciling the whole window on every late arrival — real machinery for a case that needs a
    /// settlement to lag another by days. A deterrent that occasionally undercharges is a fair trade for
    /// one that can never overcharge.
    /// </remarks>
    public int RecordAbandonment(DateTimeOffset at)
    {
        var index = _abandonments.BinarySearch(at);
        _abandonments.Insert(index < 0 ? ~index : index, at);

        var windowStart = at - LiveRules.AbandonmentWindow;
        var countInWindow = _abandonments.Count(a => a > windowStart && a <= at);

        var penalty = LiveRules.AbandonmentPenalty(countInWindow);
        if (penalty > 0) Stats = Stats with { TotalScore = Stats.TotalScore - penalty };

        PruneAbandonments();
        return penalty;
    }

    /// <summary>
    /// Drops entries that can no longer count towards any future tier — older than the window
    /// relative to the newest entry on record. Purely a lazy eviction of history, run after the tier
    /// above is already computed and applied, so it can never be the reason a penalty changes; it only
    /// keeps the list from growing forever.
    /// </summary>
    private void PruneAbandonments()
    {
        if (_abandonments.Count == 0) return;
        var newest = _abandonments[^1];
        _abandonments.RemoveAll(a => newest - a >= LiveRules.AbandonmentWindow);
    }

    /// <summary>
    /// Test-and-record: applies <paramref name="applyStatChange"/> and remembers <paramref name="matchId"/>
    /// as settled in the same call, so the two can never come apart — there is no gap where a crash
    /// could leave a stat mutation applied but unmarked, or marked but never applied. A repeat for the
    /// same id runs the mutation zero times and returns false, which is what makes a retried settlement
    /// call idempotent. Ids evict oldest-first past <see cref="MaxSettledMatchIds"/>.
    /// </summary>
    public bool TryRecordSettledMatch(string matchId, Action applyStatChange)
    {
        if (_settledMatchIds.Contains(matchId)) return false;

        applyStatChange();

        _settledMatchIds.Add(matchId);
        if (_settledMatchIds.Count > MaxSettledMatchIds) _settledMatchIds.RemoveAt(0);
        return true;
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
        new Dictionary<string, CategoryRecord>(_byCategory), [.. _friends], IsGuest, [.. _abandonments], [.. _settledMatchIds]);

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
        if (s.SettledMatchIds is not null) p._settledMatchIds.AddRange(s.SettledMatchIds);
        return p;
    }
}
