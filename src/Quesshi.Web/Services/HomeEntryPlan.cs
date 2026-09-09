namespace Quesshi.Web.Services;

/// <summary>Every way into a duel the home offers, across both tabs.</summary>
public enum HomeEntry
{
    /// <summary>Live tab, primary: open a live lobby at the last-used settings and go share its code.</summary>
    CreateLobby,

    /// <summary>Live tab: the code field revealed under "Join with a code".</summary>
    JoinWithCode,

    /// <summary>Live tab: the random live queue, with its waiting state.</summary>
    PlayAStranger,

    /// <summary>Live tab: tapping a friend's face in the online row.</summary>
    ChallengeFriend,

    /// <summary>Offline tab, "New duel" → A friend: an async duel with a code to send.</summary>
    NewDuelFriend,

    /// <summary>Offline tab, "New duel" → Anyone: an async duel matched against whoever is waiting.</summary>
    NewDuelAnyone,

    /// <summary>Offline tab, "New duel" → By code: the same code field, reached from the other tab.</summary>
    NewDuelByCode
}

/// <summary>What one entry point actually calls. One value per distinct call, not per button.</summary>
public enum HomeCall
{
    /// <summary><c>Api.CreateLiveLobbyAsync</c> — <c>POST /api/live/lobby</c>, landing on <c>/lobby/{code}</c>.</summary>
    CreateLiveLobby,

    /// <summary><c>LobbyClient.QueueRandomAsync</c> — the <c>QueueRandom</c> hub invoke.</summary>
    QueueLiveRandom,

    /// <summary><c>LobbyClient.ChallengeAsync</c> — the <c>Challenge</c> hub invoke.</summary>
    ChallengeOverHub,

    /// <summary><c>Api.CreateMatchAsync(random: false)</c> — <c>POST /api/matches</c>.</summary>
    CreateAsyncDuel,

    /// <summary><c>Api.CreateMatchAsync(random: true)</c> — the same endpoint, matched rather than invited.</summary>
    CreateAsyncRandomDuel,

    /// <summary><c>Api.InviteAsync</c> to learn the kind, then <c>Api.JoinLiveAsync</c> or
    /// <c>Api.JoinAsync</c>. One lookup, so the right endpoint is called on the first try rather than
    /// guessed by trial — the same branch <c>Join.razor</c> makes off <c>/join/{code}</c>.</summary>
    JoinByCode
}

/// <summary>
/// Which call each of the home's seven entry points makes. The screen changed completely in issue
/// #88 — two tabs, a primary card each, a picker in place of a form — but nothing underneath it did:
/// every path a player could take before still exists and still reaches the same endpoint. That is
/// the claim worth pinning down, and it is pinned here, as a table a test can read, rather than
/// spread across seven click handlers where "still reachable" could only be checked by eye.
/// <c>Home.razor</c> dispatches on <see cref="Call"/>, so the table is the code, not a description
/// of it.
/// </summary>
public static class HomeEntryPlan
{
    /// <summary>
    /// What "Create a lobby" opens with. Two seats is the overwhelmingly common case and the one the
    /// old stepper started on; seats are chosen in the lobby itself now (issue #87's decision), where
    /// the roster that will fill them is actually visible, rather than guessed at on the home.
    /// </summary>
    public const int DefaultCapacity = 2;

    public static HomeCall Call(HomeEntry entry) => entry switch
    {
        HomeEntry.CreateLobby => HomeCall.CreateLiveLobby,
        HomeEntry.PlayAStranger => HomeCall.QueueLiveRandom,
        HomeEntry.ChallengeFriend => HomeCall.ChallengeOverHub,
        HomeEntry.NewDuelFriend => HomeCall.CreateAsyncDuel,
        HomeEntry.NewDuelAnyone => HomeCall.CreateAsyncRandomDuel,

        // Both tabs' code fields land here: a code identifies a duel, and the invite lookup — not the
        // tab the player happened to be looking at — is what says whether it is live or async. Typing
        // a live code on the Offline tab therefore joins the live duel rather than failing, which is
        // the only behaviour that could be right for someone who was sent a code and pasted it.
        HomeEntry.JoinWithCode or HomeEntry.NewDuelByCode => HomeCall.JoinByCode,

        _ => throw new ArgumentOutOfRangeException(nameof(entry), entry, null)
    };

    /// <summary>Which tab offers this entry point. Only the code field appears on both.</summary>
    public static string Tab(HomeEntry entry) => entry switch
    {
        HomeEntry.NewDuelFriend or HomeEntry.NewDuelAnyone or HomeEntry.NewDuelByCode => HomeTabs.Offline,
        _ => HomeTabs.Live
    };
}
