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
- **Profile editing works for guests.** `PUT /api/me` renames and sets language for any bearer of a
  token.
- **Settlement is already per-participant.** `LiveMatchSettlement.SettleAsync` loops
  `foreach (var playerId in Participants(m))`.

## Decisions

- Both duel kinds become N-player, not just live.
- Ranked resolution: standings by score, top scorer wins, a shared top place is a draw. Teams are a
  possible future feature and are deliberately not designed for here.
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

**Expiry** is unchanged: `LiveRules.LobbyExpires` measured from `CreatedAt`, settled inside
`Advance`.

**Async lobbies work identically**, with one restriction: nobody may join after start. Late joins
would make "is this duel finished?" unanswerable, which is too high a price for saving a share-link
round trip.

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
  challenge carries a lobby code, and accepting joins that lobby. Decline and expiry are unchanged.
  This is a net deletion — the challenge stops duplicating the settings the lobby now owns.
- **Everyone else** gets the link.
- **Offline friends.** An invitation outlives the recipient's connection: it is stored against the
  lobby, delivered on their next connect, and dies when the lobby starts or expires.

### The exclusivity invariant changes

`LiveLobbyGrain` today enforces that a player holds at most one commitment across every door — a
queue entry, a challenge sent, or a challenge received — and the grain exists to make that atomic.
That is too strict once lobbies exist: being invited to three lobbies at once is normal, and none of
it is a commitment until you sit down.

So **membership in a lobby is the commitment** (still at most one per player, still arbitrated under
the grain's single lock, alongside the random queue), and **invitations become plain notifications**
with no exclusivity at all. The grain stops arbitrating invitations, which removes state and removes
refusals that would otherwise have to be explained in the UI.

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

## 3. The N-player match

**Live rounds.** `Answer` currently reveals when `round.Answers.Count >= 2`; that becomes
`== Participants.Count`. `MatchRules.QuestionTime`, `NetworkGrace`, `LiveRules.RevealTime` and
`Scoring.Score`'s speed bonus are all per-player and unchanged. The difficulty ramp is a property of
the question set, not of the player count, and is untouched.

**Abandonment** is the only genuinely new behaviour. `CloseRound` today collects players who have hit
`LiveRules.MissesBeforeAbandon` and branches on `== 2` (nobody is playing, `NoContest`) or `== 1` (the
other player wins). With N:

- a player who hits the streak is recorded as having abandoned and drops out of the active set;
- the duel continues while two or more remain;
- when one remains, they win by abandonment;
- when none remain, it is `NoContest`.

`AbandonedBy` becomes a set rather than a `string?`, which is a snapshot and `MatchDoc` change. The
existing penalty in `LiveMatchSettlement` applies per abandoner and is otherwise unchanged.

**Resolution.** `FinishResolved` stops comparing two numbers and produces **standings**: participants
ordered by score descending, ties sharing a place. `WinnerId` is the top scorer and `IsDraw` is true
when the top place is shared — both keep their present meaning, so `MatchDoc`, the results screen and
`RematchStatus` need no new concepts.

**Stats stay three-valued.** `PlayerStats(Wins, Losses, Draws, Streak, BestStreak, TotalScore)` is
untouched: the top scorer takes a win, anyone tied with them a draw, everyone else a loss. Fourth of
eight is a loss, exactly as second of two is. This is coarse on purpose; the alternative is a
placement-aware rating system, which is a different project.

**Async, N-player.** One `PlayerRun` per player as now. The match resolves when every participant's
run is finished, or when `Match.TryForfeit`'s clock ends it, at which point unfinished runs score
whatever they banked. `CanReveal` keeps its rule — you see others only once your own run is finished
— and what you then see is every participant who has also finished, with the rest marked as still
playing. Standings land when the last run does.

## 4. Guest identity and upgrade

**Editing.** `PUT /api/me` already renames and sets language for any token holder, guests included.
What is missing is the avatar: `AvatarSeed` is set to the player id at construction and has no
setter. Add `Player.SetAvatar(seed)`, carry it on `UpdateProfileDto`, and validate it against the
palette `Ranks.Tint` already derives colours from. Name and avatar seed are the whole of the editable
identity; there is nothing else on `Player` a person would want to change, and none is invented here.

**Where.** On the lobby page. A guest arriving by link lands in the lobby, so their name and avatar
are editable next to the roster they are about to appear in — no profile detour, at the one moment
they care.

**Upgrade.** Reuse OTP rather than build anything. The guest requests a code for a real address and
verifies it while holding their guest token. If the address has no account, the existing `Player`
**keeps its id** and gains the email, `IsGuest` clears, and the banked `TotalScore` enters the
leaderboard the guest was excluded from. Every match, friendship and stat survives because the row
never moved. This needs a `Player.Claim(email)` method; Mongo's unique email index handles the rest,
as `{id}@guest.invalid` is simply replaced.

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
  empty participants means an old record, so the list is built from the legacy pair. New writes emit
  the new shape only. Both branches are deleted once old duels have aged out.
- `MatchDoc` gets the same tolerance plus a one-time backfill (`Participants = [ChallengerId,
  OpponentId]`, `OwnerId = ChallengerId`). `MongoContext`'s two compound indexes — `ChallengerId` +
  `CreatedAt` descending, and `OpponentId` + `CreatedAt` descending — are then replaced by one
  multikey compound index on `Participants` + `CreatedAt` descending, and `MongoMatchArchive`'s
  `Eq(ChallengerId) || Eq(OpponentId)` filter becomes a single `AnyEq`. The unique index on `Code`,
  which is what keeps the shared async/live code namespace collision-free, is untouched.
- `AbandonedBy` migrates the same way: read either shape, write the new one.

## 6. Order of work

Each step green before the next.

1. **Domain** — `Participants`, `DuelSettings`, standings, N-way abandonment, on both match types.
   Nothing above the domain changes; the existing tests keep passing as the N=2 case, and that is the
   safety net for every step after.
2. **Persistence** — snapshots and `MatchDoc` tolerant of both shapes, backfill, index swap.
3. **Grains** — lobby phase (join, capacity, start), question set drawn at start, invitations
   retargeted at a lobby code, rematch as a new pre-invited lobby, `LiveLobbyGrain` renamed. The
   lobby *rules* stay in `LiveMatch`; `LiveMatchGrain` gains only a thin `StartAsync`, because at
   21KB it is already the largest file in the project.
4. **Server** — lobby endpoints, invitation changes on `LobbyHub`.
5. **Web** — the lobby page, roster and settings controls, standings on the results screen, guest
   name and avatar editing.
6. **Guest upgrade** — last, because it depends on none of the above and should not hold the rest up.

Steps 1 and 2 are invisible to players; the app behaves exactly as it does now until step 3. That is
deliberate — work can stop after any step and still leave a working game.

## Out of scope

- **Teams.** Wanted eventually. Nothing here should preclude it, but no team concept is designed or
  built.
- **Email invitations.** Rejected above.
- **Merging a guest into an existing account.** Refused with an explanation instead.
- **Placement-aware rating.** Stats stay win/loss/draw.
- **Friend requests with consent.** `POST /api/friends/{id}` still adds mutually and immediately.
  Independent of this work and worth its own small project.
