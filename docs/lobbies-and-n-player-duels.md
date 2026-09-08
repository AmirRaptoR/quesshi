# Lobbies and N-player duels

## Context

Today a duel is two people. `Match` and `LiveMatch` both carry a `ChallengerId` and a nullable
`OpponentId`, and every rule above them — scoring, winner resolution, abandonment, rematch, the live
hub, the duel screen — is written against that pair. Getting a second person in happens through one
of three unrelated doors: the random queue (`MatchmakingGrain` for async, `LiveLobbyGrain` for live),
a live challenge to an online friend (`LobbyHub.Challenge`), or a share code the challenger sends out
of band (`/join/{code}`).

This design collapses those into one idea: **a duel is a lobby that people join, and two players is
the case where the lobby holds two.** The lobby owns the settings, the invitations point at it, and
the match that comes out of it can have between two and eight participants, live or asynchronous.

The alternative — a separate `GroupMatch` aggregate beside the working 1v1 — was rejected. It would
mean two scoring implementations, two rematch stories and two abandonment rules kept in step
forever, and it preserves exactly the special case this is meant to remove.

## What already exists

Worth stating, because a lot of this design is deletion rather than construction.

- **The lobby is already a phase.** `LiveMatch` has `LivePhase.Lobby`, where it sits from creation
  until someone joins; `Match` has `MatchState.AwaitingOpponent` for the same span. Both already
  expire on a clock — a live lobby to `NoContest` after `LiveRules.LobbyExpires` (10 minutes, settled
  inside `Advance`), an async one to `Forfeited` after `MatchRules.ForfeitAfter` (48 hours, via
  `TryForfeit`).
- **`LiveMatch.Participants()`** exists as a private iterator yielding the challenger and, if seated,
  the opponent. It becomes the real collection.
- **The invite link works and is single-use.** `Join.razor` lands an invitee, `InviteDto.Open`
  reports whether the seat is still free, and `Match.Join` throws once the state leaves
  `AwaitingOpponent`.
- **Guests are real players.** `Player.Guest(...)` creates a `Player` with `IsGuest = true` and a
  synthetic `{id}@guest.invalid` address; `POST /api/auth/guest/{code}` and `/guest/live/{code}` mint
  one and take the seat in a single call, returning an ordinary JWT. Settlement already keeps guests
  off the leaderboard while still recording their result.
- **Profile editing does *not* work for guests.** `PUT /api/me` renames and sets language, but the
  group refuses guests unless the endpoint carries `AllowGuest`, and the `PUT` does not
  (`GameEndpoints.cs:23-28, 38`). See Section 4.
- **Settlement is already per-participant.** `LiveMatchSettlement.SettleAsync` loops
  `foreach (var playerId in Participants(m))` — but see the prerequisite below: nothing calls it.

## Async guards that do not exist, and have to

Two guards this design leans on turn out not to be there. Both are latent bugs today and become
sharper with N players, so they are part of step 1 rather than assumptions.

- **`Match.SubmitAnswer` has no terminal check.** `ServeNext` guards on `IsOver` (`Match.cs:65`);
  `SubmitAnswer` does not (`Match.cs:75-90`). An answer served before a forfeit can still land after
  it, mutating the run and re-running `TryResolve` — and `MatchGrain` computed `wasOver` as true
  (`MatchGrain.cs:88`), so nothing settles the changed result. `SubmitAnswer` must refuse a terminal
  match.
- **`Match.Join` ignores its `now` argument** (`Match.cs:47`). It checks only `State ==
  AwaitingOpponent`, so the 48-hour deadline is enforced solely by the grain's forfeit reminder; a
  late or lost reminder leaves a stale lobby joinable indefinitely. `Join` must settle the clock
  first — `TryForfeit(now)` before seating — exactly as `LiveMatch.Join` calls `Advance(now)` before
  checking state (`LiveMatch.cs:92-97`).

- **`Match.IsOver` omits `NoContest`** (`Match.cs:34`: `State is Resolved or Forfeited`). An async
  lobby cancelled by its owner therefore would not read as terminal, and `Mappers.cs:91-102` would
  not summarise it. `NoContest` joins `Match.IsOver` and the summary mapper, mirroring `LiveMatch`,
  so both aggregates end a cancelled lobby the same way.

## Prerequisite: settlement is not connected

`LiveMatchGrain.SettleAsync()` is `private Task SettleAsync() => Task.CompletedTask;`
(`src/Quesshi.Grains/LiveMatchGrain.cs:327`), carrying a TODO deferring the work to a settling
sub-issue. `LiveMatchSettlement` is written and tested but has no caller. **Live duels therefore
apply no stats, no leaderboard entry and no abandonment penalty today.**

This is a pre-existing bug, not something this design introduces, but the design must not inherit it
silently: every claim below about settlement generalising to N players assumes settlement runs at
all. It is therefore **step 0**, landed and verified before any N-player work:

- Call `LiveMatchSettlement.SettleAsync` from `LiveMatchGrain`'s `!wasOver && m.IsOver` branch.
- Fix `LiveMatchSettlement.ToArchived`, which passes `m.Id` as both id *and* code
  (`LiveMatchSettlement.cs:76`), while `LiveMatchGrain.IndexAsync` writes the same row with the real
  `m.Code`. Connected as-is, settling would overwrite a duel's share code with its id and break code
  resolution for it. The settlement mapper must use `m.Code`, and the two writers must agree on the
  whole row.
- An integration check that a finished live duel moves `PlayerStats`, the leaderboard and the
  abandonment penalty — the thing whose absence went unnoticed until now.

### Settlement has to survive a crash halfway through

`AfterChangeAsync` persists the match (`SaveAsync`, `LiveMatchGrain.cs:266`) and unregisters the
reminder (`RearmAsync`) *before* `NotifyAsync`, which is where the settlement call sits. So the
terminal state is already durable when settlement begins. If the process dies or storage fails after
two of five participants have been applied, reactivation loads a match that is already over,
`wasOver` is true, and settlement never runs again — those three players are silently never settled.
Retrying naively is no better: `LiveMatchSettlement`'s own remarks say it has no idempotency guard
and "a caller that settles twice will apply every effect twice", and `ILeaderboard.AddAsync` is an
increment, not a set.

A progress checkpoint in the grain is necessary but **not sufficient**, and it is worth being precise
about why: a checkpoint written *after* the effects still duplicates them when the crash lands
between the effect and the checkpoint, and one written *before* loses them when the crash lands the
other way. Retry safety has to live with the effect, not beside it.

**The leaderboard stops being incremental.** `ILeaderboard.AddAsync(playerId, delta)` is the only
reason settlement's leaderboard write is dangerous to repeat. But the leaderboard is a projection of
something already authoritative — `Player.Stats.TotalScore`, which `RecordResult` and
`RecordAbandonment` already move with a zero floor. So `AddAsync` and `PenaliseAsync` are replaced by
`SetAsync(playerId, total)`, called with the player's `TotalScore` after each change. Repeating it is
a no-op by construction, the floor stops being duplicated in two places, and `PenaliseAsync`'s
"two penalties racing past the floor" problem disappears with the method.

**One player operation per match, not two.** Settlement today calls `ApplyResultAsync` and then, for
a quitter, `RecordAbandonmentAsync` — two writes for one player's settlement. A single settled-match
marker cannot guard both: recording the result would mark the match settled and turn the abandonment
penalty into a no-op. Rather than key the marker by `(matchId, effectKind)` and keep the two-call
dance, the two collapse into one grain call, applied as a single mutation:

```
SettleMatchAsync(string matchId, MatchOutcome? outcome, int score,
                 List<string> categoryIds, List<bool> correct,
                 DateTimeOffset? abandonedAt)
```

**Both effects are optional, because `NoContest` needs a penalty with no result.** `MatchOutcome` is
`{ Win, Loss, Draw }` — it has no value meaning "nothing happened" — and the `NoContest` rule above
requires penalising an abandoner while leaving wins, losses, draws and answer stats untouched. So a
null `outcome` records no result and no answer stats, and a null `abandonedAt` applies no penalty.
Every combination is one atomic mutation under one marker.

**`abandonedAt` is an event time, not a processing time.** `Player.RecordAbandonment(now)` uses its
argument twice: it prunes entries older than `LiveRules.AbandonmentWindow` (seven days) and appends
`now` as the moment this abandonment counts from, and `LiveRules.AbandonmentPenalty` scales with how
many are inside the window. A boolean plus wall-clock would mean a retry an hour later — or a
recovery a day later — computes a different penalty and extends how long it counts against the
player. The caller passes the match's `EndedAt`, which is fixed the moment the duel ends and is
therefore identical on every retry. `RecordAbandonmentAsync`'s penalty return value disappears with
the merge: the penalty moves `TotalScore`, and the leaderboard projection below reads that.

**A stable timestamp is necessary but not sufficient — the pruning rule has to change too.**
`RecordAbandonment` prunes with `now - at >= LiveRules.AbandonmentWindow` against the timestamp it is
handed, then appends and counts (`Player.cs:73-80`). Settlements do not arrive in event order: a duel
that ended on Monday can settle after one that ended on Wednesday, whether through a retry, a
recovery, or two grains finishing at once. Pruning against Monday removes nothing, and the tier is
then computed over a list that no longer means "abandonments in the last seven days".

So the list becomes order-independent: insert `at` in timestamp order, and compute the tier from the
entries lying in `[at - AbandonmentWindow, at]` — the window around the *event*, not around whenever
the code happens to run. Pruning becomes a separate, purely-lazy concern (drop entries older than the
window relative to the newest entry) and can never change a penalty. With the dedup marker preventing
a second insert for the same match, no abandonment is ever counted twice and no penalty is ever
inflated by arrival order.

*Ceiling, stated rather than papered over:* this makes the penalty **never overcharged**, not fully
order-independent. If Wednesday's duel settles before Monday's, Wednesday's tier was computed over a
list that did not yet contain Monday, so it may be one tier low; inserting Monday afterwards does not
retroactively raise it. Making that exact would mean storing each event's applied penalty and
reconciling the whole window on every late arrival — real machinery for a case that needs a settlement
to lag another by days. The penalty is a deterrent, and a deterrent that occasionally undercharges is
a fair trade for one that can never overcharge.

**Stats deduplicate on the player document.** That one call records the match id in a capped list of
settled match ids on `Player`, written in the *same* `UpsertAsync` as the stat change — one Mongo
write, so the marker and the effect cannot come apart. A second call for the same match id skips the
mutation.
*Ceiling:* the list is capped (200 entries, oldest evicted), so a duel settling later than 200
subsequent duels for the same player could double-apply. That is far outside the retry window this
guards, and the alternative is an unbounded list on a hot document.

**The leaderboard projection is written unconditionally, outside the dedup guard.** Mongo and Redis
are two stores: the stat write and its marker can commit and the `SetAsync` that follows can still
fail. If the retry saw the marker and skipped everything, the leaderboard would stay stale forever
while the match happily marked itself settled. So the order inside the grain call is: apply the stats
if the marker says they are not applied, then **always** project `Stats.TotalScore` with `SetAsync`,
then return. Repeating an absolute write costs nothing, which is precisely why it can be
unconditional — a duplicate call is what repairs the leaderboard rather than what corrupts it.

**A failed write must not leave the cache believing it succeeded.** `PlayerGrain` mutates `_player`
and *then* awaits `players.UpsertAsync(_player)`. When that write fails, the activation is left
holding stats and a settled-match marker Mongo never stored — and a retry inside the same activation
would read that marker and skip an effect that was never persisted. So every write path invalidates
the cache on a failed or ambiguous write: `_player` is dropped and reloaded from the repository
before the next use, and the failure is rethrown so the caller retries against a truthful cache. The
test that matters here is a retry *within one activation*, not across a reactivation.

**With those two, the grain checkpoint is only an optimisation** — it saves re-walking participants,
and its loss can no longer corrupt anything. It stays for that reason: `SettledPlayers` plus a
`SettlementComplete` flag in grain state. `archive.SaveAsync` is a whole-row upsert and was always
safe to repeat.

### Settlement needs a retry trigger, not just reactivation

Resuming on activation is not enough. `RearmAsync` unregisters the safety-net reminder as soon as the
match is over, so a transient failure leaves the grain alive with nothing scheduled; once it
deactivates, nothing brings it back — no player reopens a finished duel. So the reminder is
unregistered on `SettlementComplete`, not on `IsOver`, and until then it keeps firing settlement.

**`MatchGrain` has the same window and gets the same treatment.** Async settlement sits at
`MatchGrain.cs:95` under the same `!wasOver && _match.IsOver` shape, after its own state write, and
its forfeit reminder is unregistered once the match is over (`MatchGrain.cs:114`). The merged grain
call, the dedup marker, the unconditional projection and the reminder held until
`SettlementComplete` all apply to it identically. Step 0 covers **both** match types; fixing only the
live side would leave the async path carrying the exact defect this section exists to remove.

Tests, and they are the point of all of this: fail between two participants' effects; fail between an
effect and the checkpoint save; then recover **without anyone touching the match**, and assert every
participant is settled exactly once.

## Decisions

- Both duel kinds become N-player, not just live.
- Ranked resolution: standings by score, top scorer wins, a shared top place is a draw for its
  sharers. Per-player outcomes come from the standings list, not from the scalar `WinnerId`/`IsDraw`.
  Teams are a possible future feature and are deliberately not designed for here.
- An abandoner ranks below every player who finished, regardless of score, and is penalised on the
  strength of having abandoned rather than on the match's final state.
- Every mutation of a `Player` goes through `IPlayerGrain` — profile edits, bans, results, the
  leaderboard and the guest claim. The repository has one writer.
- The leaderboard becomes a projection of `Player.Stats.TotalScore` written absolutely, not a running
  increment, which is what makes settlement safe to retry.
- An invitation's lifetime is its lobby's lifetime: ten minutes live, 48 hours async.
- Settings are editable exactly while the question set is empty. Questions are drawn at Start, which
  is also what makes a legacy record with recorded answers safe to convert.
- Lobby capacity is 2–8.
- A live round advances when everyone has answered or the 20s clock expires — the existing rule, over
  N instead of two.
- No email invitations. A lobby is reached by its share link or by an in-app invite; sending mail to
  arbitrary addresses on a user's say-so is a spam vector the link already makes unnecessary.
- One language per duel. The lobby can change which one before the duel starts.
- Guests keep their server-side `Player` row and gain an upgrade path onto the same id.

## 1. Lobby lifecycle

`Match` and `LiveMatch` replace `ChallengerId` + `OpponentId?` with:

- `OwnerId` — who created it, and the only one who may change settings or press Start.
- `Participants` — an ordered list, `Participants[0] == OwnerId`.
- `Capacity` — 2 to 8, fixed at creation.

**Joining** is allowed while the match is in its lobby phase and not full. The outcomes are the ones
`LiveJoinResult` already names — `Joined`, `AlreadyIn` (idempotent), `SelfJoin`, `Taken`, `Expired` —
plus `Full`. Anyone holding the link may take a seat, as today.

**Starting.** A live duel currently starts the instant the second player joins. With N somebody has
to say go, so: the owner presses Start once at least two people are in, *or* it auto-starts when
`Participants.Count == Capacity`. For a capacity-2 lobby those are the same event, which is why 1v1
behaviour is unchanged and needs no special case. Joins close at start.

**Leaving before start** frees the seat. The owner leaving ends the lobby as `NoContest`; ownership
does not transfer.

**Expiry differs by kind, and each kind keeps the deadline it has today.** A live lobby expires
`LiveRules.LobbyExpires` (10 minutes) after `CreatedAt` and settles to `NoContest` inside `Advance`.
An async lobby expires `MatchRules.ForfeitAfter` (48 hours) after `CreatedAt` and settles to
`Forfeited` via `TryForfeit`. Neither is changed by this design; the difference is deliberate, since a
live lobby is a room people are standing in and an async one is an open invitation. Every "the lobby
expires" below means whichever of those two applies, and an invitation is dead the moment its lobby
is — there is no window in which an invitation outlives the thing it points at.

**Async lobbies otherwise work identically**, with one restriction: nobody may join after start. Late
joins would make "is this duel finished?" unanswerable, which is too high a price for saving a
share-link round trip.

### Settings live on the lobby

Today the question set is chosen before the match exists: `Home.razor` picks language, count,
categories and levels, and `LiveMatch.Create(id, code, lang, challengerId, questionIds, now)` receives
the finished list and validates its size. A lobby that can change those has to defer selection.

- A new `DuelSettings` record — language, question count, categories, levels — lives on the match from
  creation.
- `Create` no longer takes `questionIds`. The set is drawn at **Start**, from whatever the settings
  say at that instant.
- `MatchRules.IsValidCount` moves from a creation-time guard to a settings-validity guard, so an
  invalid combination is rejected as it is typed rather than when the duel begins.
- **Nothing may be served before Start.** `Match.ServeNext` creates a `PlayerRun` on demand
  (`Match.cs:63-72`) and an owner sitting alone in a lobby could otherwise serve themselves a run —
  which, with a question count of zero, is `Finished` the moment it exists (`PlayerRun.cs:12-14`),
  permanently. Serving requires a drawn question set, so it is refused while `QuestionIds` is empty.
  Legacy records arrive with questions already drawn and therefore behave exactly as they do today.
- `LiveMatchSnapshot`, `MatchSnapshot` and `MatchDoc` carry the settings alongside the question ids,
  which are empty until start.

Only the owner may change settings, and nothing can change after start because the questions are
already drawn.

### The lobby page

`/lobby/{code}`, for both duel kinds. It shows the roster, the settings, and — for the owner — the
settings controls and Start. It replaces the "waiting for an opponent" state that `Duel.razor` and
the live page show today. Live lobbies push roster and settings changes over the existing hub; async
lobbies need the same push, which is the one place this design adds plumbing rather than reusing it.

## 2. Invitations

A lobby already has everything an invitation needs: a `Code` and a link. An invitation is therefore
never a thing that *creates* a duel — it is a pointer at a lobby that already exists.

- **In-app, for friends with accounts.** `LiveLobbyGrain`'s challenge machinery is retargeted:
  instead of carrying language, count, categories and levels and building a duel on accept, a
  challenge carries a lobby code, and accepting joins that lobby. Decline is unchanged. This is a net
  deletion — the challenge stops duplicating the settings the lobby now owns.
- **Everyone else** gets the link.
- **Offline friends.** An invitation outlives the recipient's connection: it is stored against the
  lobby and delivered on their next connect.

**In-app invitations cannot reach everyone, and the design must not pretend they can.** Two hard
limits in the current code:

- `LobbyHub.OnConnectedAsync` **aborts guest connections outright** (`LobbyHub.cs:29-37`) — "nothing
  on this hub is ever open to a guest", deliberately. A guest can therefore never receive an in-app
  invitation, however the invitation is modelled.
- Challenges are friendship-gated (`LobbyHub.cs:100-107`), so a random opponent you have just played
  is not reachable by one.

Both matter most for rematch auto-invite, whose participants are frequently neither friends nor
account holders. So:

- **Guests are invited by link only.** The rematch lobby's code *is* that link, it is shown to every
  participant on the results screen, and no in-app delivery is attempted for a guest. This costs
  nothing, because a guest already reached the original duel by link.
- **Invitations are permitted between former co-participants**, not only between friends — a narrow,
  explicit relaxation of the friendship gate, scoped to people who have demonstrably just played
  together. Anyone else still needs the friendship.

**An invitation lives exactly as long as its lobby.** `LiveLobbyGrain.ChallengeLifetime` is 45
seconds today, which is right for "your friend is online this second, answer now" and wrong for an
invitation that is meant to wait for someone offline — a friend connecting a minute later would find
it already dead. Since the invitation is now only a pointer, it has no business expiring before the
thing it points at: `ChallengeLifetime` is deleted, and an invitation's expiry *is* its lobby's, as
defined in Section 1 — ten minutes for a live lobby, 48 hours for an async one, and immediately on
Start. One lifetime, applied identically to storage, reconnect delivery and acceptance.

### The exclusivity invariant changes

`LiveLobbyGrain` today enforces that a player holds at most one commitment across every door — a
queue entry, a challenge sent, or a challenge received — and the grain exists to make that atomic.
That is too strict once lobbies exist: being invited to three lobbies at once is normal, and none of
it is a commitment until you sit down.

So **invitations become plain notifications** with no exclusivity at all. The grain stops arbitrating
invitations, which removes state and removes refusals that would otherwise have to be explained in
the UI.

**Membership exclusivity is best-effort; seat uniqueness is not.** It is tempting to move the "one at
a time" invariant onto lobby membership and keep enforcing it under the matchmaking grain's lock, but
that lock cannot reach: the seat lives in a different grain (`LiveMatchGrain.JoinAsync`,
`LiveMatchGrain.cs:78-90`), and Orleans gives no transaction spanning the two, so a membership record
written under the lock and a seat taken elsewhere can always disagree. Rather than pretend otherwise,
the invariants are split by who can actually guarantee them:

- **A seat is taken at most once** — guaranteed, because `LiveMatch.TryJoin` decides it inside the
  single grain that owns that match, which is where it was always decided.
- **A player sits in at most one lobby** — best-effort. It is checked when joining and surfaced in
  the UI, but a determined race can leave someone in two. That is not a correctness problem: they can
  only play one at a time, and the other lobby expires or starts without them, which the abandonment
  rules already handle.

The matchmaking grain keeps its single-lock job for the random queue, where the thing being handed
out *is* the grain's own state and the lock genuinely covers it.

The grain is renamed `ILiveMatchmakingGrain` / `LiveMatchmakingGrain`. Its current name collides with
the per-duel lobby this design introduces, and it is really the matchmaking lobby.

### Rematch

`RequestRematchAsync`'s symmetric two-player readiness protocol does not generalise — "everyone must
press ready before it expires" gets worse with each additional player. It is replaced by: **a rematch
creates a new lobby with the same settings and auto-invites every participant of the finished duel.**
Whoever turns up, plays. This deletes the readiness handling in `RematchOutcome` rather than growing
it, and it is the same code path as any other invitation.

It also gives "duel these people again" from the duels list for free — that is a rematch of a
finished match.

**One rematch lobby per finished duel, without recording a reference.** "A rematch creates a lobby"
cannot mean *every* request creates one: two participants pressing Rematch at the same moment would
each own a lobby and invite the other. Recording the created lobby's code on the finished match — the
obvious fix — has its own hole: the lobby is created before the reference can be written
(`LiveMatchGrain.cs:188-192`), so a crash in between orphans a lobby and lets the next request create
a second one.

Instead the rematch lobby's id is **derived from the finished match's id** rather than minted. Every
request computes the same id, and `ILiveMatchGrain.CreateAsync` is already documented idempotent —
"returns the existing view if the duel already exists" — so the first request creates it and every
later one, from anyone, however many times, lands on the same lobby with no reference to store and
nothing to orphan. Once that lobby starts or expires, a rematch *of it* derives a further id, so the
chain continues without collision.

This is worth being clear about because the guarantee is **new**, not inherited: today's
`RequestRematchAsync` is idempotent about *readiness* only — after creating a rematch it clears
readiness and keeps no reference to what it made (`LiveMatchStateRecord` holds just `RematchReadyAt`),
so a second pair of presses creates a second duel.

## 3. The N-player match

**Live rounds.** `Answer` currently reveals when `round.Answers.Count >= 2`; that becomes **the count
of *active* participants** — everyone who has not abandoned. Counting all participants would mean
that once one player of three drops out, the two who remain could both answer and still sit through
the full 20s timeout on every remaining round, because the third answer can never arrive. Answers
from an abandoned player are refused, so the active count can only shrink.
`MatchRules.QuestionTime`, `NetworkGrace`, `LiveRules.RevealTime` and
`Scoring.Score`'s speed bonus are all per-player and unchanged. The difficulty ramp is a property of
the question set, not of the player count, and is untouched.

**Abandonment** is the only genuinely new behaviour. `CloseRound` today collects players who have hit
`LiveRules.MissesBeforeAbandon` and branches on `== 2` (nobody is playing, `NoContest`) or `== 1` (the
other player wins). With N:

- a player who hits the streak is recorded as having abandoned and drops out of the active set;
- the duel continues while two or more remain;
- when one remains, they win by abandonment;
- when none remain, it is `NoContest`.

`AbandonedBy` becomes an **ordered list of (player id, round slot)** rather than a `string?` — not a
plain set, because standings need to know who lasted longer and a set does not encode that. It is
appended in `CloseRound`, so the round slot is recorded for free. Snapshot and `MatchDoc` change
accordingly.

**An abandoner is ranked last, not by score.** Otherwise the winning move in a three-player duel is
to build a lead and walk away: the duel would carry on without you and still rank you first. So
**Eliminating a player needs a wire contract, not just a domain rule.** `LiveRoundRevealDto` and
`LivePlayerRoundDto` describe a round for two named sides, and `Live.razor:372-410` renders answering
from that. Nothing in the current contract can say "this player is out". Without one, an eliminated
player's client keeps offering the answer buttons and the survivors keep seeing a slot that will
never fill. So the round DTOs become per-participant collections and gain an explicit elimination
event, delivered when `CloseRound` drops someone: the eliminated client switches to a spectating
view, and everyone else stops waiting on that seat. This is contract and UI work in steps 4 and 5, and
it is the part of N-player abandonment that is not free.

Standings place all non-abandoners by score above all abandoners, and abandoners take the places
below, ordered by round slot descending — lasting longer places better. Several players can hit the
streak in the same `CloseRound`; they share a round slot and therefore **share a place**, exactly as
tied scores do. Their outcome is `Loss` and their banked score is 0 — which is the
rule `LiveMatchSettlement` already applies to a quitter, now applied to every abandoner rather than
to the single `AbandonedBy`.

**The penalty is keyed off the abandoners set, never off `MatchState`.** Today it reads
`m.State == MatchState.Abandoned && m.AbandonedBy == playerId` (`LiveMatchSettlement.cs:~46`), which
under N-player rules would silently skip the abandoner in the common case: one player of three drops
out, the other two play to the end, and the duel finishes `Resolved`, not `Abandoned`. `MatchState`
keeps its present meaning — `Abandoned` is "only one active player was left" — and eligibility for
the penalty becomes simply "is this player in the abandoners set".

**`NoContest` applies the penalty only when abandonment is what caused it.** Today two players
abandoning is a `NoContest` and neither is penalised; with N that makes mass abandonment the cheapest
way to dodge the penalty entirely. But "penalise whoever is in the abandoners set" is too broad, and
only becomes wrong at N > 2: `FinishNoContest` is also reached from the stale-round check
(`LiveMatch.cs:143-147`, a process that was away) and from lobby expiry, and in a three-player duel
one player can have legitimately abandoned *before* an outage forces the duel to `NoContest`. Keying
on the set alone would then punish that player for the server's failure.

So `NoContest` records **why**: `AllAbandoned`, `Stale`, or `LobbyExpired`. The penalty applies on
`AllAbandoned` only.

**The staleness test has to be widened, or the reason code makes things worse.** `Advance`'s guard is
`Phase == LivePhase.Question && CurrentRound is { Answers.Count: 0 } && now - round.StartedAt >
StaleAfter` (`LiveMatch.cs:143-147`). An outage that begins in `Countdown` or `Reveal` — or in
`Question` after a single player has answered — misses it entirely, and the `while (StepOnce(now))`
loop below then simulates every remaining round with nobody answering. At
`LiveRules.MissesBeforeAbandon = 3`, three simulated rounds mark **every** player abandoned. Today
that lands on `NoContest` with nobody penalised, so it is invisible; under the rule above it would
become `AllAbandoned` and bill every player for the server's downtime. The reason code would convert
a latent bug into a live one.

So staleness stops being a special case of one phase: if `now` is past the current phase's deadline
by more than `StaleAfter`, the process was away, whatever the phase and whoever had answered. That
boundary already exists as `NextDueAt`, which is defined for every phase and is the same value
`StepOnce` steps against, so the two cannot drift. `Lobby` keeps its own reason (`LobbyExpired`)
since a lobby passing its deadline is expiry, not absence.

This also changes an existing behaviour deliberately: a round where one player had answered before
the process went away is now `Stale` rather than simulated to a conclusion. That is the conservative
direction — no stats, no penalties, no result — and it is the honest reading of a duel nobody was
present for. `Stale` and `LobbyExpired` leave `PlayerStats` exactly as they found it, abandoners
in the set or not — the original guarantee that a duel lost to a restart costs nobody anything,
now stated in terms that survive a third player. Stats, leaderboard and standings remain suppressed
for every `NoContest`; only the `AllAbandoned` penalty crosses.

**Resolution produces standings, and standings are what everything reads.** `FinishResolved` stops
comparing two numbers and produces an ordered `Standings` list, one `Standing` per participant:
player id, score, place (ties share a place), and that player's own `MatchOutcome`.

The scalar `WinnerId` and `IsDraw` **cannot** carry per-player outcomes any more and must not be used
to derive them. Today `OutcomeFor` reads `m.IsDraw ? Draw : (m.WinnerId == playerId ? Win : Loss)`
(`LiveMatchSettlement.cs:70`, mirrored at `MatchGrain.cs:131`), and `Mappers.cs:96` and `:131` render
a global `"draw"`. For scores of 100, 100, 50 the intended result is two draws and one loss; a global
`IsDraw` would hand all three players a draw. So:

- `WinnerId` is the sole holder of first place, and **null when first place is shared**.
- `IsDraw` is true when first place is shared — it means "nobody won outright", not "everybody drew".
- Every per-player outcome — settlement, the results screen, the duels list, `RematchStatus` — reads
  `Standings`, never the two scalars. `OutcomeFor` is deleted rather than generalised.

**The archive has to hold more than two scores.** `ArchivedMatch` is
`(… string ChallengerId, string? OpponentId, string? WinnerId, bool IsDraw, int ChallengerScore, int
OpponentScore, …)` — exactly two participants and exactly two scores. It gains a
`List<ParticipantResult>` (player id, score, place, outcome) that replaces the two score fields;
`MatchDoc` stores it, and a legacy row reads back as two `ParticipantResult`s built from the old
fields. This is the same expand-and-contract as the participant migration and rides along with it.

**Stats stay three-valued.** `PlayerStats(Wins, Losses, Draws, Streak, BestStreak, TotalScore)` is
untouched: first place alone is a win, first place shared is a draw for each sharer, everything below
is a loss. Fourth of eight is a loss, exactly as second of two is. This is coarse on purpose; the
alternative is a placement-aware rating system, which is a different project.

**Async, N-player.** One `PlayerRun` per player as now. The match resolves when every participant's
run is finished, or when `Match.TryForfeit`'s clock ends it, at which point unfinished runs score
whatever they banked.

While the match is running, `CanReveal` keeps its rule — you see others only once your own run is
finished — and what you then see is every participant who has also finished, with the rest marked as
still playing.

**Forfeiture finalises everything.** Standings cannot wait for "the last run to finish" when
forfeiture is precisely the case where some run never will, and a player who never finished cannot be
held behind their own `CanReveal` forever. So the terminal state is authoritative: forfeiture
computes standings from whatever each player banked, and results are revealed to **everyone**,
finished or not. This is the rule `MatchGrain` already applies — `var reveal = m.CanReveal(forPlayerId)
|| m.IsOver;` (`MatchGrain.cs:172`) — carried into the N-player standings rather than left to apply
only to the two-score view. Unfinished runs are marked **expired**, not "still playing": the duel is
over, and showing a live-looking state for a run that can never resume is a lie the UI would have to
keep telling.

## 4. Guest identity and upgrade

**Editing.** `PUT /api/me` renames and sets language — but **not for guests**. The endpoint group
refuses any guest request unless the endpoint carries `AllowGuest` metadata
(`GameEndpoints.cs:25-26`), and the `PUT` at `GameEndpoints.cs:38` does not carry it; `GET /me` at
line 36 does. Adding avatar fields and a lobby control would still return 403. So the change is
threefold: `.WithMetadata(new AllowGuest())` on the `PUT`, `Player.SetAvatar(seed)` plus the avatar
field on `UpdateProfileDto` validated against the palette `Ranks.Tint` already derives colours from,
and an HTTP-level test that a guest token can actually change its name and avatar. Name and avatar
seed are the whole of the editable identity; nothing else on `Player` is worth changing, and none is
invented here.

**Where.** On the lobby page. A guest arriving by link lands in the lobby, so their name and avatar
are editable next to the roster they are about to appear in — no profile detour, at the one moment
they care.

**Upgrade.** Reuse the OTP *challenge*, but not `VerifyOtpAsync`. That method ends with
`GetOrCreateAsync(normalized, …)` (`AuthService.cs:35`), which registers a brand-new player id for
the address on the spot — so routing the upgrade through it would create the second account the
upgrade exists to avoid, and the guest's id would be stranded beside it. The upgrade gets its own
verify path: validate the challenge exactly as `VerifyOtpAsync` does (including the attempt-count
save and the delete on success), then, instead of `GetOrCreateAsync`, check the address is unused and
claim it onto the caller's existing guest id.

The guest requests a code for a real address and verifies it while holding their guest token. If the address has no account, the existing `Player`
**keeps its id** and gains the email, `IsGuest` clears, and the banked `TotalScore` enters the
leaderboard the guest was excluded from. Every match, friendship and stat survives because the row
never moved. This needs a `Player.Claim(email)` method, and Mongo's unique email index handles the
collision case, as `{id}@guest.invalid` is simply replaced.

**The upgrade must go through `IPlayerGrain`, not the repository.** `PlayerGrain` loads the whole
`Player` into `_player` on activation, never reloads it, and every write is a full-document
`players.UpsertAsync(_player)`. An upgrade written straight to the repository would be reverted the
moment that grain next writes — the cached copy still carries `{id}@guest.invalid` and
`IsGuest = true`, and the next settled match would put them back. So `IPlayerGrain` gains
`ClaimEmailAsync`, which claims the address, clears the guest flag and refreshes the leaderboard under
the grain's own serialisation, and the endpoint calls that.

**The leaderboard write must move into the grain too.** Settlement currently updates the leaderboard
itself, outside `PlayerGrain`, and reads `IsGuest` through the repository to decide whether to. That
interleaves: settlement banks 100 into a guest's stats, an upgrade seeds the leaderboard from the new
total, then settlement — holding its own stale view — writes the same 100 again. Result application,
the guest-eligibility check and the leaderboard write therefore all happen inside `PlayerGrain`, for
both match types, so they are serialised against each other and against the claim. `SetAsync`'s
absolute value makes the residual interleavings harmless rather than merely unlikely.

**The upgrade has to replace the session token.** `TokenIssuer` stamps the guest claim from
`player.IsGuest` at issue time (`TokenIssuer.cs:25`) and authorization reads that claim, not the
database — so clearing the flag while the browser still holds the old JWT leaves the upgraded player
guest-forbidden from every account-only endpoint and from `LobbyHub`. The upgrade therefore returns a
full sign-in result exactly as `/api/auth/otp/verify` does: a fresh token with no guest claim, plus
the player. The client stores it, `AppState` re-applies it (clearing `IsGuest` and the guest match
keys), and `MainLayout.SyncLobbyConnectionAsync` reconnects the lobby hub with the new token — which
it already does on any state change, so this needs no new plumbing, only the correct state change.

**This is a pre-existing bug wider than the upgrade.** Two endpoints write player documents behind
the grain's back:

- `PUT /api/me` (`GameEndpoints.cs:47`), so a rename or language change can be silently undone by the
  next match that settles;
- `POST /admin/users/{id}/ban` (`AdminEndpoints.cs:239`), which is worse — a later grain write can
  **undo a ban**, and an overlapping admin write can just as easily overwrite a profile change or a
  claimed email.

Both move onto `IPlayerGrain` (`UpdateProfileAsync`, `SetBannedAsync`). Only then is the claim about
a single writer true, and only then is the ban durable. After this, every mutation of a `Player` goes
through `IPlayerGrain` and the repository has exactly one writer.

**When the address already has an account**, the upgrade is refused with a clear message and ordinary
sign-in is offered instead; the guest history stays behind. Merging two player rows means reconciling
two stat lines, two friend sets and two histories against matches that reference both ids — its own
project, and not one to start by accident.

## 5. Migration

All grain state lives in Redis under the `"hot"` provider, including `MatchGrain`, which holds async
duels that can legitimately be in flight for days. This cannot be a "restart and lose it" migration
the way an in-flight live duel already can be. Expand and contract:

- `MatchSnapshot` and `LiveMatchSnapshot` keep `ChallengerId` and `OpponentId` as legacy optional
  fields beside the new `OwnerId`, `Participants` and `Settings`. `FromSnapshot` gains one branch:
  empty participants means an old record, so the list is built from the legacy pair.

### A legacy async record is a lobby whose questions are already drawn

Converting the participant fields is not enough. `Match.ServeNext` and `SubmitAnswer` guard only on
"is a participant" and "is not over" — there is no `InProgress` requirement — so a challenger can
play their **entire run while the match is still `AwaitingOpponent`**. A converted record can
therefore hold a full question set and recorded answers while sitting in what the new lifecycle calls
the lobby phase. Treating it as an editable lobby would redraw the questions underneath answers
already scored against them; treating it as started would lock out the opponent who was invited and
never arrived.

The rule that resolves this needs no new state: **settings are editable exactly while `QuestionIds`
is empty**, not "while in the lobby phase". A new lobby is created with no questions and draws them
at Start; a legacy record arrives with its questions already drawn. So a legacy match keeps its
question set, its runs and its state, still accepts a joiner into its free seat while
`AwaitingOpponent`, and simply presents as a lobby whose settings are read-only. `DuelSettings` for
such a record is reconstructed from what is knowable — language and question count from the existing
set — with categories and levels left empty, which is only ever displayed, never used to draw.

This same rule is what keeps a live legacy record coherent: it too has its questions and cannot have
them changed.
- `MatchDoc` gets the same tolerance plus a one-time backfill: `OwnerId = ChallengerId`, and
  `Participants = [ChallengerId]` **plus `OpponentId` only when it is not null**. A legacy lobby
  nobody joined has a null opponent, and including it would invent a phantom second participant —
  which would both corrupt its standings and contradict the free seat the record is meant to keep
  open. The same filter applies to snapshot conversion, and participant results are constructed only
  for participants that exist. `MongoContext`'s two compound indexes — `ChallengerId` +
  `CreatedAt` descending, and `OpponentId` + `CreatedAt` descending — are then replaced by one
  multikey compound index on `Participants` + `CreatedAt` descending, and `MongoMatchArchive`'s
  `Eq(ChallengerId) || Eq(OpponentId)` filter becomes a single `AnyEq`. The unique index on `Code`,
  which is what keeps the shared async/live code namespace collision-free, is untouched.
- `AbandonedBy` migrates the same way: read either shape, write the new one.

### The compatibility readers stay

The original plan was to delete the legacy branches "once old duels have aged out". They never do.
Nothing in `src/Quesshi.Grains` calls `ClearStateAsync` — a finished match's grain state stays in
Redis indefinitely — and the async history listing reactivates those grains to build its rows
(`GameEndpoints.cs:286` fans out `GetGrain<IMatchGrain>` over archived rows). A snapshot written
before this change can therefore be read years later, long past any match deadline.

So the readers are permanent, not transitional. They are one branch each, which is cheaper than the
alternative: an offline rewrite of every retained grain state in Redis, with no natural moment to run
it and nothing to fall back on if it is wrong. If they are ever to be removed, it takes that explicit
Redis migration — waiting will not make it safe, and the spec should not pretend otherwise.

## 6. Order of work

Each step green before the next — which is a promise the steps cannot keep on their own. Replacing
`ChallengerId`/`OpponentId`, changing `Create`'s signature and swapping `ArchivedMatch`'s two score
fields breaks every grain, mapper and test that names them, all of which live in later steps. Keeping
step 1 green therefore requires **temporary compatibility adapters, written as part of step 1 and
deleted in step 8**:

- `ChallengerId` and `OpponentId` survive as computed accessors over `Participants` (`Participants[0]`
  and `Participants.ElementAtOrDefault(1)`), marked obsolete.
- `Create` keeps its current overload, delegating to the settings-based one with a capacity of 2 and
  the question set pre-drawn.
- `ArchivedMatch` keeps `ChallengerScore`/`OpponentScore` as computed projections of the first two
  participant results.
- `AbandonedBy` keeps a `string?` accessor over the new ordered list (the first entry, or null),
  because `LiveMatchGrain.cs:442`, `LiveEnded.cs:5` and `LiveView.cs:23` all type it that way and are
  migrated in later steps.

Each adapter dies with the step that migrates its last consumer, and step 8 fails the build if any
survive. Without them the sequence is not a sequence — it is one commit pretending to be eight.

0. **Connect settlement, both kinds** — the prerequisite above. Call `LiveMatchSettlement` from
   `LiveMatchGrain`, fix its archive mapper's code field, give both `LiveMatchGrain` and `MatchGrain`
   durable per-player settlement progress with resume-on-activation and a reminder held until
   `SettlementComplete`, merge the two player writes into one `SettleMatchAsync`, and move the
   leaderboard to an absolute projection. Prove with tests that a finished duel of either kind moves
   stats, the leaderboard and the abandonment penalty, and that a crash halfway through settles the
   remainder exactly once. Everything after this assumes settlement runs.
1. **Domain** — `Participants`, `DuelSettings`, standings, N-way abandonment, the `NoContest` reason,
   and the three missing async guards (`SubmitAnswer` terminal check, `Join` settling its clock,
   `NoContest` in `IsOver`), on both match types.
   Nothing above the domain changes; the existing tests keep passing as the N=2 case, and that is the
   safety net for every step after.
2. **Persistence** — snapshots and `MatchDoc` tolerant of both shapes, `ArchivedMatch` carrying
   participant results, backfill, index swap.
3. **Grains** — lobby phase (join, capacity, start), question set drawn at start, invitations
   retargeted at a lobby code, rematch as a new pre-invited lobby, `LiveLobbyGrain` renamed. The
   lobby *rules* stay in `LiveMatch`; `LiveMatchGrain` gains only a thin `StartAsync`, because at
   21KB it is already the largest file in the project.
4. **Server** — lobby endpoints, invitation changes on `LobbyHub` including the co-participant
   relaxation, and the N-way round DTOs with the elimination event.
5. **Web** — the lobby page, roster and settings controls, the eliminated/spectating state, standings
   on the results screen, guest name and avatar editing.
6. **Player writes onto the grain** — `PUT /api/me` and the admin ban endpoint through `IPlayerGrain`,
   `AllowGuest` on the `PUT`, the avatar field, and the leaderboard moved inside the grain as an
   absolute `SetAsync`. Independent of the lobby work and fixes two live bugs, so it can move earlier
   — and step 0 wants the `SetAsync` change anyway, so in practice that part lands with step 0.
7. **Guest upgrade** — depends only on step 6 and should not hold the rest up.
8. **Delete the adapters** — the obsolete accessors, the legacy `Create` overload and the archive
   score projections listed above. Nothing but this step removes them, and leaving them is how a
   two-shape domain becomes permanent.

Step 0 is visible — live duels start affecting stats and the leaderboard, which they should have been
doing all along. Steps 1 and 2 are invisible; the app otherwise behaves exactly as it does now until
step 3. That is deliberate — work can stop after any step and still leave a working game, though
stopping before step 8 leaves the adapters in place.

## Out of scope

- **Teams.** Wanted eventually. Nothing here should preclude it, but no team concept is designed or
  built.
- **Email invitations.** Rejected above.
- **Merging a guest into an existing account.** Refused with an explanation instead.
- **Placement-aware rating.** Stats stay win/loss/draw.
- **Friend requests with consent.** `POST /api/friends/{id}` still adds mutually and immediately.
  Independent of this work and worth its own small project.
