using Microsoft.Extensions.Logging;
using System.Text.Json;
using Orleans;
using Orleans.Runtime;
using Quesshi.Grains.Abstractions;
using Quesshi.Application.Ports;
using Quesshi.Application.UseCases;
using Quesshi.Domain;

namespace Quesshi.Grains;

/// <summary>
/// Runs one live duel. Unlike <see cref="MatchGrain"/>, time passes here whether or not a player is
/// looking: a grain timer armed for <see cref="LiveMatch.NextDueAt"/> drives every phase change, a
/// coarse reminder is the restart safety net once the timer dies with the activation, and
/// <see cref="ILiveNotifier"/> is the one door state leaves through — the grain never knows whether
/// SignalR, a test fake, or nothing at all is listening on the other side.
/// </summary>
public sealed class LiveMatchGrain(
    [PersistentState("live", "hot")] IPersistentState<LiveMatchStateRecord> state,
    IQuestionRepository questions,
    ICategoryRepository categories,
    ILiveNotifier notifier,
    IMatchArchive archive,
    ILiveDirectory directory,
    QuestionSetBuilder questionSetBuilder,
    LiveMatchSettlement settlement,
    IIdFactory ids,
    IClock clock,
    IPlayerRepository players,
    ILogger<LiveMatchGrain> logger) : Grain, ILiveMatchGrain, IRemindable
{
    private const string SafetyNetReminder = "live-safety-net";

    /// <summary>Mirrors <c>LiveMatchmakingGrain.MaxCodeAttempts</c>: how many fresh codes a rematch's lobby will try before giving up.</summary>
    private const int MaxCodeAttempts = 5;
    private static readonly TimeSpan ReminderPeriod = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MinimumDueTime = TimeSpan.FromMilliseconds(1);

    /// <summary>Headroom on top of the timer's due time, so a slightly-late tick still finds the activation alive.</summary>
    private static readonly TimeSpan DeactivationSlack = TimeSpan.FromSeconds(5);

    private LiveMatch? _match;
    private IGrainTimer? _timer;

    /// <summary>How many rounds have already had <c>RoundStarted</c>/<c>RoundRevealed</c> emitted, in this activation's lifetime.</summary>
    private int _startedThrough;
    private int _revealedThrough;

    /// <summary>How many entries of <c>LiveMatch.Abandoners</c> have already had <c>PlayerEliminated</c>
    /// emitted, in this activation's lifetime — the same "already announced, don't replay" bookkeeping
    /// <see cref="_startedThrough"/>/<see cref="_revealedThrough"/> do for rounds.</summary>
    private int _eliminatedThrough;

    public override async Task OnActivateAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(state.State.Json)) return;

        _match = LiveMatch.FromSnapshot(JsonSerializer.Deserialize<LiveMatchSnapshot>(state.State.Json)!);
        // Whatever this snapshot already contains was already announced before we deactivated —
        // reactivating must not replay it, only pick the clock back up from here.
        _startedThrough = _match.Rounds.Count;
        _revealedThrough = ClosedRoundCount(_match);
        _eliminatedThrough = _match.Abandoners.Count;

        var phaseBefore = _match.Phase;
        var wasOver = _match.IsOver;
        _match.Advance(clock.Now); // fast-forward through anything missed while deactivated

        // NotifyAsync's own "!wasOver && m.IsOver" transition below only fires the instant IsOver
        // flips from false to true — which, for a match that was already over when this activation
        // loaded it, happened in whatever earlier activation set SettlementComplete to false and then
        // failed to finish (or in this same activation, earlier, if ReceiveReminder's own retry below
        // is what got interrupted). That transition will never fire again for this duel, so resuming
        // has to happen here instead, before anything else is served from this activation.
        await ResumeSettlementIfNeededAsync(wasOver);

        await AfterChangeAsync(phaseBefore, wasOver);
    }

    /// <summary>
    /// Retries a settlement a transient failure left unfinished — <paramref name="wasOver"/> false
    /// means nothing to resume (a fresh transition, if this call turns out to be one, is
    /// <see cref="NotifyAsync"/>'s own job a moment later), and <c>SettlementComplete</c> anything but
    /// exactly <c>false</c> means either there is nothing left to do (<c>true</c>) or this is a
    /// pre-upgrade record no code may ever touch again (<c>null</c> — see
    /// <see cref="LiveMatchStateRecord"/>'s own remarks). Called from two places for two different
    /// failure shapes: <see cref="OnActivateAsync"/> covers a crash that took the whole activation down,
    /// and <see cref="ReceiveReminder"/> covers a failure that did not — the activation stayed alive
    /// with nothing else left to call back in and retry, which is exactly why the safety-net reminder
    /// stays armed until settlement finishes rather than dropping the moment the match is merely over.
    /// </summary>
    private Task ResumeSettlementIfNeededAsync(bool wasOver)
        => wasOver && state.State.SettlementComplete == false ? SettleAsync() : Task.CompletedTask;

    public async Task<LiveView> CreateAsync(string code, int lang, string challengerId, List<string> questionIds)
    {
        if (_match is not null) return await ViewAsync(_match, challengerId);

        _match = LiveMatch.Create(this.GetPrimaryKeyString(), code, (Language)lang, challengerId, questionIds, clock.Now);

        // A lobby nobody joins must still expire even with the grain deactivated, so the reminder
        // is registered here rather than waiting for the first phase transition.
        await this.RegisterOrUpdateReminder(SafetyNetReminder, ReminderPeriod, ReminderPeriod);
        await AfterChangeAsync(LivePhase.Lobby, false);
        await IndexAsync(); // mirrored so the code is resolvable at all — a grain nobody has indexed can never be found
        return await ViewAsync(_match, challengerId);
    }

    /// <summary>The lobby-aware create path: see the interface's own remarks. Shares everything past
    /// construction with <see cref="CreateAsync"/> — only how the domain object itself is built differs.</summary>
    public async Task<LiveView> CreateLobbyAsync(string code, string ownerId, int lang, int questionCount,
        List<string> categoryIds, List<int> levels, int capacity)
    {
        if (_match is not null) return await ViewAsync(_match, ownerId);

        var settings = DuelSettings.Create((Language)lang, questionCount, categoryIds, [.. levels.Select(l => (Difficulty)l)]);
        _match = LiveMatch.Create(this.GetPrimaryKeyString(), code, ownerId, settings, capacity, clock.Now);

        await this.RegisterOrUpdateReminder(SafetyNetReminder, ReminderPeriod, ReminderPeriod);
        await AfterChangeAsync(LivePhase.Lobby, false);
        await IndexAsync();
        return await ViewAsync(_match, ownerId);
    }

    public async Task<int> JoinAsync(string playerId)
    {
        if (_match is null) return (int)LiveJoinResult.Unknown;

        var phaseBefore = _match.Phase;
        var wasOver = _match.IsOver;

        // The join that fills the last seat also starts the duel, synchronously, inside TryJoin
        // itself — exactly as it always has for a capacity-2 lobby (see LiveMatch.Join's own remarks).
        // That means the question set has to already exist the instant this call is made: DrawQuestions
        // refuses once the duel has left the lobby phase, which this join is about to do. Drawing here,
        // one join early, is what lets a bigger lobby's very last join behave identically to a 1v1's
        // second one — the legacy, pre-drawn creation path never hits this (QuestionIds is never empty
        // there), so it costs that path nothing.
        if (_match.QuestionIds.Count == 0 && !_match.IsParticipant(playerId) && _match.Participants.Count + 1 == _match.Capacity)
            await DrawQuestionsAsync();

        // TryJoin settles the clock first even on a call that is then refused (the lobby may have
        // just expired) — that still has to be persisted, so every outcome but SelfJoin — which
        // touches nothing — goes through AfterChangeAsync.
        var result = _match.TryJoin(playerId, clock.Now);
        if (result != LiveJoinResult.SelfJoin) await AfterChangeAsync(phaseBefore, wasOver);
        if (result == LiveJoinResult.Joined) await IndexAsync(); // the opponent is now part of the row a code resolves to

        return (int)result;
    }

    /// <summary>Draws this lobby's question set from its own <c>Settings</c> and hands it to
    /// <see cref="LiveMatch.DrawQuestions"/> — the one bit of IO the domain cannot do for itself.
    /// Shared by <see cref="JoinAsync"/>'s auto-start branch and <see cref="StartAsync"/>.</summary>
    private async Task DrawQuestionsAsync()
    {
        var m = _match!;
        var set = await questionSetBuilder.BuildAsync(m.Settings.Language, m.Settings.CategoryIds, m.Settings.QuestionCount, m.Settings.Levels);
        m.DrawQuestions([.. set.Select(q => q.Id)]);
    }

    public async Task<bool> StartAsync(string playerId)
    {
        if (_match is null || _match.Phase != LivePhase.Lobby) return false;
        if (playerId != _match.OwnerId || _match.Participants.Count < 2) return false;

        var phaseBefore = _match.Phase;
        if (_match.QuestionIds.Count == 0) await DrawQuestionsAsync();
        if (!_match.Start(playerId, clock.Now)) return false;

        await AfterChangeAsync(phaseBefore, false);
        return true;
    }

    public async Task<bool> LeaveAsync(string playerId)
    {
        if (_match is null) return false;
        if (playerId == _match.OwnerId) return await EndByOwnerAsync(playerId, "left by owner");

        var phaseBefore = _match.Phase;
        if (!_match.Leave(playerId, clock.Now)) return false;

        await AfterChangeAsync(phaseBefore, false);
        await IndexAsync(); // the roster shrank; mirrors JoinAsync's own index refresh on a successful join
        return true;
    }

    public async Task<bool> UpdateSettingsAsync(string playerId, int lang, int questionCount, List<string> categoryIds, List<int> levels)
    {
        if (_match is null) return false;

        DuelSettings settings;
        try
        {
            settings = DuelSettings.Create((Language)lang, questionCount, categoryIds, [.. levels.Select(l => (Difficulty)l)]);
        }
        catch (ArgumentException)
        {
            return false; // an invalid combination refuses the change outright, same as at creation
        }

        if (!_match.UpdateSettings(playerId, settings)) return false;

        await SaveAsync();
        return true;
    }

    public Task<bool> CancelAsync(string playerId) => EndByOwnerAsync(playerId, "cancelled by owner");

    /// <summary>
    /// Shared by <see cref="CancelAsync"/> and <see cref="LeaveAsync"/>'s owner branch: ends the lobby
    /// as a no-contest with <see cref="NoContestReason.OwnerCancelled"/> — never <see cref="NoContestReason.LobbyExpired"/>,
    /// which would misreport a deliberate departure as a clock running out. Owner-only and lobby-only,
    /// exactly like the original <c>CancelAsync</c> this preserves the contract of.
    /// </summary>
    private async Task<bool> EndByOwnerAsync(string playerId, string reason)
    {
        if (_match is null || playerId != _match.OwnerId || _match.Phase != LivePhase.Lobby) return false;

        var phaseBefore = _match.Phase;
        _match.EndNoContest(clock.Now, NoContestReason.OwnerCancelled);
        await AfterChangeAsync(phaseBefore, false, reason);
        return true;
    }

    public async Task<bool> AnswerAsync(string playerId, int slot, int choiceIndex)
    {
        if (_match is null || _match.IsOver) return false;
        if (slot < 0 || slot >= _match.QuestionIds.Count) return false;

        var question = await questions.GetAsync(_match.QuestionIds[slot]);
        if (question is null) return false;

        var correct = question.IsCorrect(choiceIndex);
        var phaseBefore = _match.Phase;
        var wasOver = _match.IsOver;
        var answeredBefore = _match.CurrentRound?.Answers.Count ?? 0;

        try
        {
            // LiveMatch.Answer calls Advance first, so an answer arriving after the buzzer is
            // treated as late rather than scored into a round that has already closed.
            _match.Answer(playerId, slot, choiceIndex, correct, clock.Now, question.Level);
        }
        catch (InvalidOperationException)
        {
            await AfterChangeAsync(phaseBefore, wasOver);
            return false;
        }

        question.RecordServed(correct);
        await questions.UpsertAsync(question);

        // Only the first answer of a round gets this push — the second one closes the round, and
        // RoundRevealedAsync (fired from AfterChangeAsync below) supersedes it.
        if (answeredBefore == 0 && _match.Phase == LivePhase.Question)
            await SafeNotifyAsync(() => notifier.OpponentAnsweredAsync(_match.Id, slot, playerId));

        await AfterChangeAsync(phaseBefore, wasOver);
        return true;
    }

    public async Task<LiveView?> GetAsync(string forPlayerId)
    {
        if (_match is null || !_match.IsParticipant(forPlayerId)) return null;
        return await ViewAsync(_match, forPlayerId);
    }

    public async Task EndAsync(string reason)
    {
        if (_match is null || _match.IsOver) return;

        var phaseBefore = _match.Phase;
        _match.EndNoContest(clock.Now);
        await AfterChangeAsync(phaseBefore, false, reason);
    }

    /// <summary>
    /// Replaces the old symmetric ready-flag handshake: a rematch now creates a lobby with this
    /// duel's own settings and capacity, and auto-invites every participant — whoever turns up, plays.
    /// Refused outright for a non-participant, a duel that is not over, or one that never got past a
    /// single seat (nothing to rematch with).
    /// </summary>
    /// <remarks>
    /// The lobby's id is <em>derived</em> from this duel's id (<see cref="DeriveRematchLobbyId"/>),
    /// never minted. That is the whole mechanism behind "one rematch lobby per finished duel, however
    /// many participants press the button, however many times": every request computes the same id,
    /// and <see cref="ILiveMatchGrain.CreateLobbyAsync"/> is already documented idempotent — it
    /// returns the existing view once the duel already exists — so the first request to actually reach
    /// that grain creates it and every later one, from anyone, lands on that same lobby with nothing
    /// recorded here and nothing to orphan. Recording the created lobby's id on this match instead — the
    /// obvious-looking alternative — cannot be made crash-safe: the lobby is created inside
    /// <c>CreateLobbyAsync</c> before this method could ever persist a reference to it, so a crash in
    /// between would orphan a lobby and let the next request mint a second one. Deriving sidesteps
    /// that by never needing a reference in the first place. A rematch of the rematch derives a
    /// further id from <em>its</em> id, so the chain keeps extending without ever colliding with
    /// itself or with any freshly minted match id (see <see cref="DeriveRematchLobbyId"/>).
    /// </remarks>
    public async Task<RematchOutcome> RequestRematchAsync(string playerId)
    {
        if (_match is null || !_match.IsOver || !_match.IsParticipant(playerId) || _match.Participants.Count < 2)
            return new RematchOutcome((int)RematchStatus.Refused);

        if (!await GrainFactory.GetGrain<ILiveSettingsGrain>(0).IsEnabledAsync())
        {
            await SafeNotifyAsync(() => notifier.RematchFailedAsync(_match.Id));
            return new RematchOutcome((int)RematchStatus.Failed);
        }

        var lobbyId = DeriveRematchLobbyId(_match.Id);
        var lobbyView = await CreateRematchLobbyAsync(lobbyId);
        if (lobbyView is null)
        {
            await SafeNotifyAsync(() => notifier.RematchFailedAsync(_match.Id));
            return new RematchOutcome((int)RematchStatus.Failed);
        }

        await InviteOthersAsync(lobbyView, playerId);
        await SafeNotifyAsync(() => notifier.RematchCreatedAsync(_match.Id, lobbyId, lobbyView.Code));
        return new RematchOutcome((int)RematchStatus.Created, lobbyId, lobbyView.Code);
    }

    /// <summary>
    /// A rematch lobby's id, deterministic from the finished match's own — see
    /// <see cref="RequestRematchAsync"/>'s remarks for why that is the entire mechanism keeping
    /// concurrent rematch requests from producing more than one lobby. The suffix can never collide
    /// with a freshly minted match id: every id this codebase mints (<c>IIdFactory.NewId</c>) is a bare
    /// hex GUID with no punctuation, so nothing but a string this method itself produced can end in
    /// "-rematch". A rematch of a rematch appends the suffix again, so the chain keeps extending
    /// ("...-rematch-rematch") without ever colliding with an earlier link in it.
    /// </summary>
    private static string DeriveRematchLobbyId(string matchId) => $"{matchId}-rematch";

    /// <summary>
    /// Opens the derived lobby with this duel's own settings and capacity, owned by this duel's own
    /// owner regardless of which participant's request actually triggers the creation — deliberately
    /// not "whoever asked first", so ownership never depends on a race between concurrent callers and
    /// stays the same however many times, or by whom, this is called. <c>CreateLobbyAsync</c> is
    /// idempotent, so a call that lands after the lobby already exists simply gets the existing view
    /// back, its own (wasted) code and owner argument ignored. Mints a fresh code per attempt the same
    /// way <c>LiveMatchmakingGrain.BuildDuelAsync</c> does; unlike that method, a collision here only
    /// matters for whichever call turns out to be the one that actually creates the lobby, but every
    /// call mints one anyway since there is no way to know in advance which call that will be.
    /// </summary>
    private async Task<LiveView?> CreateRematchLobbyAsync(string lobbyId)
    {
        var m = _match!;
        for (var attempt = 0; attempt < MaxCodeAttempts; attempt++)
        {
            var code = ids.NewMatchCode();
            if (await archive.ByCodeAsync(code) is not null) continue;

            var grain = GrainFactory.GetGrain<ILiveMatchGrain>(lobbyId);
            return await grain.CreateLobbyAsync(code, m.OwnerId, (int)m.Lang, m.Settings.QuestionCount,
                [.. m.Settings.CategoryIds], [.. m.Settings.Levels.Select(l => (int)l)], m.Capacity);
        }

        return null;
    }

    /// <summary>
    /// Every non-guest participant of the finished duel gets a plain in-app invitation to the rematch
    /// lobby, except its actual owner (who already knows — they either created it just now or already
    /// own it from an earlier request) and <paramref name="requesterId"/> (who already has
    /// <paramref name="lobbyView"/>'s id and code from this very call's own return value). A repeat
    /// rematch press by someone other than the owner re-sends invitations to everyone else, which is a
    /// harmless duplicate notification rather than a correctness problem — see
    /// <c>ILiveMatchmakingGrain</c>'s own remarks on why an invitation carries no exclusivity to
    /// violate.
    ///
    /// A guest is skipped here entirely, not merely left to fail: <c>LobbyHub.OnConnectedAsync</c>
    /// aborts every guest connection outright, so a challenge minted for one would sit in
    /// <c>PendingForAsync</c> forever, never delivered, never accepted. Guests already reached this
    /// duel by link, so they are reached the same way for its rematch — the lobby's share code goes
    /// out on <see cref="NotifyAsync"/>'s own <c>RematchCreatedAsync</c> push instead, to the whole
    /// finished duel's group, which a guest participant is connected to by definition.
    /// </summary>
    private async Task InviteOthersAsync(LiveView lobbyView, string requesterId)
    {
        var matchmaking = GrainFactory.GetGrain<ILiveMatchmakingGrain>(0);
        var lobbyOwnerId = lobbyView.Participants[0];
        foreach (var participantId in _match!.Participants)
        {
            if (participantId == lobbyOwnerId || participantId == requesterId) continue;

            var participant = await players.GetAsync(participantId);
            if (participant is { IsGuest: true }) continue;

            await SafeNotifyAsync(() => matchmaking.ChallengeAsync(ids.NewId(), lobbyOwnerId, participantId, lobbyView.Id));
        }
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (_match is null) return;

        var phaseBefore = _match.Phase;
        var wasOver = _match.IsOver;
        _match.Advance(clock.Now);

        // The reminder is not only the safety net for a lobby nobody joins or a stalled clock — once
        // settlement starts, it is also the only thing left standing between a transient storage
        // failure and a duel that never finishes settling without a deactivation in between. A crash
        // that takes the whole activation down is resumed by OnActivateAsync when it comes back; a
        // failure that does not is retried right here, every time this fires, until SettlementComplete
        // is true and RearmAsync finally lets the reminder go.
        await ResumeSettlementIfNeededAsync(wasOver);

        await AfterChangeAsync(phaseBefore, wasOver);
    }

    private async Task OnTimerTickAsync(CancellationToken ct)
    {
        if (_match is null || _match.IsOver) return;

        var phaseBefore = _match.Phase;
        var wasOver = _match.IsOver;
        // A wall-clock/TimeProvider rounding race can fire this with nothing yet due; Advance is a
        // no-op then, and AfterChangeAsync still re-arms rather than leaving the duel with no tick.
        _match.Advance(clock.Now);
        await AfterChangeAsync(phaseBefore, wasOver);
    }

    /// <summary>
    /// The one door every state-changing entry point leaves through: persist, re-arm the clock, then
    /// notify. Notification is last on purpose — a notifier that throws leaves the state and the
    /// clock intact.
    /// </summary>
    private async Task AfterChangeAsync(LivePhase phaseBefore, bool wasOver, string? endReason = null)
    {
        MarkSettlementOwedOnFreshTransition(wasOver);
        await SaveAsync();
        // A duel that is already over is about to have its row removed in NotifyAsync's ending
        // branch below — writing it here first would only be undone a moment later.
        if (!_match!.IsOver) await UpsertDirectoryAsync();
        await RearmAsync();
        await NotifyAsync(phaseBefore, wasOver, endReason);
    }

    private async Task NotifyAsync(LivePhase phaseBefore, bool wasOver, string? endReason)
    {
        var m = _match!;

        if (phaseBefore == LivePhase.Lobby && m.Phase == LivePhase.Countdown)
            await SafeNotifyAsync(() => notifier.CountdownStartedAsync(m.Id, BuildCountdown(m)));

        for (var i = 0; i < m.Rounds.Count; i++)
        {
            if (i >= _startedThrough)
            {
                var round = m.Rounds[i];
                var question = await questions.GetAsync(round.QuestionId);
                if (question is null)
                {
                    // The round already opened in the domain against a question id nothing can
                    // resolve — an operational data problem, not a player's fault. End it rather
                    // than leave the duel wedged with a card nobody can ever build.
                    logger.LogWarning(
                        "Question {QuestionId} for round {Slot} of live duel {MatchId} could not be resolved; ending as no-contest.",
                        round.QuestionId, round.Slot, m.Id);
                    if (!m.IsOver)
                    {
                        m.EndNoContest(clock.Now);
                        MarkSettlementOwedOnFreshTransition(wasOver: false); // this ends it, so it was not over a moment ago
                    }
                    await SaveAsync();
                    await RearmAsync();
                    break;
                }

                var category = await categories.GetAsync(question.CategoryId);
                await SafeNotifyAsync(() => notifier.RoundStartedAsync(m.Id, BuildRoundCard(round, question, category, m.QuestionIds.Count)));
                _startedThrough = i + 1;
            }

            if (i < ClosedRoundCount(m) && i >= _revealedThrough)
            {
                var reveal = await BuildRoundRevealAsync(m, i);
                await SafeNotifyAsync(() => notifier.RoundRevealedAsync(m.Id, reveal));
                _revealedThrough = i + 1;
            }
        }

        // Elimination is discovered inside CloseRound, which only ever runs as part of closing a
        // round — so by the time the reveal loop above has caught this activation up on every
        // newly-closed round, every abandoner CloseRound found along the way is already in
        // m.Abandoners too. Announced after the reveal it happened alongside, not before: a client
        // sees "here is what happened in that round" and only then "and that is who it cost".
        for (var i = _eliminatedThrough; i < m.Abandoners.Count; i++)
        {
            var elimination = m.Abandoners[i];
            await SafeNotifyAsync(() => notifier.PlayerEliminatedAsync(m.Id, new LivePlayerEliminated(elimination.PlayerId, elimination.RoundSlot)));
        }
        _eliminatedThrough = m.Abandoners.Count;

        if (!wasOver && m.IsOver)
        {
            await IndexAsync(); // the row has to read NoContest/Resolved/Abandoned before anyone can be told
            await RemoveFromDirectoryAsync(); // a finished duel never lingers in the in-flight list
            await SettleAsync();
            await SafeNotifyAsync(() => notifier.EndedAsync(m.Id, BuildEnded(m, endReason)));
        }
    }

    /// <summary>
    /// Everything that happens once, when a duel ends: history, stats, leaderboard, the abandonment
    /// penalty. Called both from the fresh "!wasOver &amp;&amp; m.IsOver" transition in
    /// <see cref="NotifyAsync"/> and, on resume, from <see cref="OnActivateAsync"/> — by the time
    /// either calls in, <see cref="SaveAsync"/> has already made sure <c>SettlementComplete</c> is
    /// <c>false</c> rather than its legacy-reading <c>null</c> default, so the only thing left to
    /// guard here is a defensive re-entry once it is already <c>true</c>.
    ///
    /// <see cref="LiveMatchSettlement.SettleAsync"/> is itself safe to call more than once for the
    /// same duel — its own remarks explain why — so a crash between two participants, or between a
    /// participant and the <c>SettledPlayers</c> checkpoint that records it, loses nothing: resuming
    /// just re-walks whoever is not yet in that set, and the ones already there cost one skipped
    /// iteration rather than a repeated effect.
    /// </summary>
    private async Task SettleAsync()
    {
        var m = _match!;
        if (state.State.SettlementComplete == true) return;

        await settlement.SettleAsync(m, m.Lang, state.State.SettledPlayers, async playerId =>
        {
            state.State.SettledPlayers.Add(playerId);
            await SaveAsync();
        });

        state.State.SettlementComplete = true;
        await SaveAsync();

        // Only safe to let the safety-net reminder go once settlement has actually finished — see
        // RearmAsync's own remarks for why IsOver alone is not enough to decide that.
        await RearmAsync();
    }

    private async Task RearmAsync()
    {
        _timer?.Dispose();
        _timer = null;

        if (_match is null || _match.IsOver)
        {
            // The reminder is the only thing that can ever bring this grain back once it deactivates,
            // so it stays armed for as long as settlement has started but not finished
            // (SettlementComplete == false) — dropping it the instant the match is merely over, as
            // this once did, leaves a transient settlement failure with nothing left to retry it, and
            // no player reopens a finished duel to trigger a reactivation by hand. Both "finished"
            // (true) and "settled long ago by code that never wrote this field at all" (null, see
            // LiveMatchStateRecord) have nothing left for the reminder to do.
            if (_match is not null && state.State.SettlementComplete == false) return;

            if (await this.GetReminder(SafetyNetReminder) is { } reminder)
                await this.UnregisterReminder(reminder);
            return;
        }

        var due = _match.NextDueAt!.Value - clock.Now;
        if (due < MinimumDueTime) due = MinimumDueTime;

        _timer = this.RegisterGrainTimer(OnTimerTickAsync, new GrainTimerCreationOptions
        {
            DueTime = due,
            Period = Timeout.InfiniteTimeSpan,
            KeepAlive = true
        });

        this.DelayDeactivation(due + DeactivationSlack);
    }

    private async Task SafeNotifyAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ILiveNotifier threw for live duel {MatchId}; the duel keeps running.", _match?.Id);
        }
    }

    /// <summary>Writes this duel's row to the in-flight index — everything <c>/admin/live</c> needs
    /// without activating this grain. Called on every phase change while the duel is still running.
    /// Every seated player via <see cref="Participants"/>, not the old two-scalar pair: the same
    /// truncation this helper already exists to fix everywhere else it feeds a view or a notification.</summary>
    private Task UpsertDirectoryAsync()
    {
        var m = _match!;
        var row = new LiveDirectoryRow(m.Id, m.Code, [.. Participants(m)], (int)m.Lang,
            m.CurrentRound?.Slot ?? m.Rounds.Count, m.QuestionIds.Count, (int)m.Phase, m.CreatedAt);
        return SafeDirectoryAsync(() => directory.UpsertAsync(row));
    }

    private Task RemoveFromDirectoryAsync() => SafeDirectoryAsync(() => directory.RemoveAsync(_match!.Id));

    /// <summary>Same treatment as <see cref="SafeNotifyAsync"/>: the index is a nicety for admins, not
    /// something a Redis blip is allowed to wedge the duel over.</summary>
    private async Task SafeDirectoryAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ILiveDirectory threw for live duel {MatchId}; the duel keeps running.", _match?.Id);
        }
    }

    private Task SaveAsync()
    {
        state.State.Json = JsonSerializer.Serialize(_match!.ToSnapshot());
        return state.WriteStateAsync();
    }

    /// <summary>
    /// The one place a "not over" to "over" transition is allowed to open the settlement checkpoint —
    /// deliberately keyed on the transition itself (<paramref name="wasOver"/> false, <c>_match.IsOver</c>
    /// now true) rather than on <c>_match.IsOver</c> alone. Checking <c>IsOver</c> alone would also fire
    /// for a pre-upgrade record that was <em>already</em> over the moment it was loaded — reading its
    /// legacy <c>null</c> as "never settled" and flipping it to <c>false</c> the instant this code
    /// merely looks at it, which is exactly the mass double-settlement <see cref="LiveMatchStateRecord"/>
    /// exists to prevent. A resumed activation always calls in with <paramref name="wasOver"/> already
    /// <c>true</c>, so it is a guaranteed no-op here — resuming an interrupted settlement is
    /// <see cref="OnActivateAsync"/>'s own, separate concern.
    /// </summary>
    private void MarkSettlementOwedOnFreshTransition(bool wasOver)
    {
        if (!wasOver && _match!.IsOver) state.State.SettlementComplete = false;
    }

    /// <summary>Mirrors the duel into Mongo so its code can be resolved and its lifecycle read without
    /// activating the grain — exactly what <c>MatchGrain.IndexAsync</c> does for an async match.
    /// Scores stay 0 here even once the duel is over: <see cref="LiveMatchSettlement.IndexAsync"/>
    /// overwrites the same row with the real ones moments later, from inside <see cref="SettleAsync"/>,
    /// so this method never needs to know what a live row's score means.</summary>
    private Task IndexAsync()
    {
        var m = _match!;
        return archive.SaveAsync(new ArchivedMatch(m.Id, m.Code, m.Lang, m.ChallengerId, m.OpponentId, m.WinnerId, m.IsDraw,
            [.. Participants(m).Select(pid => new ParticipantResult(pid, 0, 0, MatchOutcome.Loss))],
            m.State, m.CreatedAt, m.EndedAt, [.. m.QuestionIds], IsLive: true));
    }

    /// <summary>How many of the current rounds are closed (revealed or done) — every round but the
    /// last while it is still open for answers, or all of them otherwise.</summary>
    private static int ClosedRoundCount(LiveMatch m) => m.Phase == LivePhase.Question ? Math.Max(0, m.Rounds.Count - 1) : m.Rounds.Count;

    /// <summary>
    /// Every seated player, not just the first two: <see cref="LiveMatch.Participants"/> directly,
    /// rather than the obsolete <c>ChallengerId</c>/<c>OpponentId</c> pair this used to yield. A
    /// capacity-&gt;2 duel is fully N-player at the domain and grain-API level already; this helper
    /// feeding every view/notification builder below off the two-scalar compatibility accessors
    /// instead was the one place that silently truncated it back down to two on the way out — round
    /// reveals, the in-flight view, and the archived row all read this.
    /// </summary>
    private static IReadOnlyList<string> Participants(LiveMatch m) => m.Participants;

    private static LiveCountdown BuildCountdown(LiveMatch m) => new(m.PhaseEndsAt!.Value, m.ChallengerId, m.OpponentId!, m.QuestionIds.Count);

    private static LiveRoundCard BuildRoundCard(LiveRound round, Question question, Category? category, int totalRounds) => new(
        round.Slot, totalRounds, question.Id, question.Prompt, [.. question.Choices],
        question.CategoryId, category?.NameFor(question.Lang) ?? question.CategoryId,
        category?.Icon ?? "", category?.Color ?? "", question.Level, question.Media,
        round.StartedAt, round.StartedAt + MatchRules.QuestionTime);

    private async Task<LiveRoundReveal> BuildRoundRevealAsync(LiveMatch m, int index)
    {
        var round = m.Rounds[index];
        var question = await questions.GetAsync(round.QuestionId);

        var players = Participants(m).Select(pid =>
        {
            var answer = round.Answers.TryGetValue(pid, out var a) ? a : new LiveAnswer(-1, false, 0, 0);
            var total = m.Rounds.Take(index + 1).Sum(r => r.Answers.TryGetValue(pid, out var ra) ? ra.Score : 0);
            return new LivePlayerRound(pid, answer.ChoiceIndex, answer.Correct, answer.Score, total);
        }).ToList();

        return new LiveRoundReveal(round.Slot, question?.CorrectIndex ?? -1, question?.Explanation, players,
            round.StartedAt + MatchRules.QuestionTime + LiveRules.RevealTime);
    }

    private static LiveEnded BuildEnded(LiveMatch m, string? reason) => new(
        m.State, m.WinnerId, m.IsDraw, m.AbandonedBy,
        [.. Participants(m).Select(pid => new LivePlayerScore(pid, m.Score(pid), CorrectCount(m, pid)))],
        [.. m.Standings],
        reason);

    private static int CorrectCount(LiveMatch m, string playerId) => m.Rounds.Count(r => r.Answers.TryGetValue(playerId, out var a) && a.Correct);

    /// <summary>
    /// The fairness rule for a reconnecting client: the round in flight never gives up the correct
    /// index, and an opponent's choice appears only once the round has closed.
    /// </summary>
    private async Task<LiveView> ViewAsync(LiveMatch m, string forPlayerId)
    {
        var closedCount = ClosedRoundCount(m);
        var closedIds = m.Rounds.Take(closedCount).Select(r => r.QuestionId).Distinct().ToList();
        var byId = closedIds.Count == 0
            ? new Dictionary<string, Question>()
            : (await questions.GetManyAsync(closedIds)).ToDictionary(q => q.Id);

        var rounds = new List<LiveRoundResultView>();
        for (var i = 0; i < m.Rounds.Count; i++)
        {
            var round = m.Rounds[i];
            var closed = i < closedCount;
            var correctIndex = closed && byId.TryGetValue(round.QuestionId, out var q) ? q.CorrectIndex : (int?)null;

            var answers = Participants(m).Select(pid =>
            {
                var answered = round.HasAnswered(pid);
                var visible = closed || pid == forPlayerId;
                var answer = answered && visible ? round.Answers[pid] : null;
                return new LiveRoundAnswerView(pid, answered, answer?.ChoiceIndex, visible ? answer?.Correct : null, answer?.Score ?? 0);
            }).ToList();

            rounds.Add(new LiveRoundResultView(round.Slot, round.QuestionId, round.StartedAt, correctIndex, answers));
        }

        var players = Participants(m).Select(pid => new LivePlayerView(
            pid,
            m.Rounds.Take(closedCount).Sum(r => r.Answers.TryGetValue(pid, out var a) ? a.Score : 0),
            m.Rounds.Take(closedCount).Count(r => r.Answers.TryGetValue(pid, out var a) && a.Correct),
            m.MissStreak(pid))).ToList();

        return new LiveView(
            m.Id, [.. Participants(m)], (int)m.State, (int)m.Phase, m.PhaseEndsAt,
            m.CurrentRound?.Slot ?? m.Rounds.Count, m.QuestionIds.Count, players, rounds,
            m.WinnerId, m.IsDraw, m.AbandonedBy, m.CreatedAt, m.EndedAt, m.Code, (int)m.Lang);
    }
}
