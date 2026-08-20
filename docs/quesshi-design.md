# Quesshi — bilingual async trivia duels

## Context

Greenfield. A multiplayer question game for **scattered friends** — people who know each other
but are never online at the same moment. That fact drives everything: matches are
**asynchronous turn-based duels**, not live rooms. Nobody waits for anybody; the notification
*is* the game ("Amir scored 480 — your turn").

| Decision | Choice |
|---|---|
| Format | Async duel; both players get the identical question set |
| Platform | Web only, installable PWA (native later via MAUI Blazor Hybrid, same Razor components) |
| Client | Blazor WebAssembly PWA |
| Backend | ASP.NET Core + Microsoft Orleans (co-hosted silo) |
| Storage | Redis (Orleans clustering, hot match state, leaderboards) + MongoDB (durable) |
| Code style | **Clean architecture** — Domain / Application / Infrastructure / Server |
| Content | Seeded by hand + a **scheduled AI generation job**, all human-approved before use |
| Languages | Persian (RTL) + English |
| Auth | **Passwordless**: Google OAuth + email OTP |
| Admin | Blazor admin panel: users, categories, questions, media, generation runs |
| Method | TDD — domain and application layers test-first |

## Game rules

- A match is **6, 9 or 12 questions** — the challenger picks — over categories they choose or three
  drawn at random, all in the challenger's language.
- **Difficulty ramps** across the whole run: the five levels are spread evenly from the first slot
  to the last, so six questions still run 1 · 2 · 3 · 3 · 4 · 5.
- **4 choices** per question, always exactly one correct.
- **20 seconds** per question (media questions need reading/watching time), enforced server-side.
- Score = `100 × correct + up to 60 speed bonus`, linear on remaining time.
- Both players face the **same 6 questions in the same order**. Player 2 sees nothing about
  player 1's answers until their own run is finished.
- Unanswered for **48 hours** → forfeit (Orleans reminder).

### Levels
`1 Easy · 2 Medium · 3 Hard` — three is enough to shape a match; more is unreviewable content debt.

### Categories (seeded)
Geography ★, History ★, Common Knowledge, Music ★, Food & Cooking ★, Movies, Science,
Nature & Animals, Literature, Art, Technology, Sport. (★ = house favourites, seeded deepest.)
The bank ships about a thousand hand-written questions per language, split into one
`questions.<lang>.<category>.json` file per topic; the seeder reads every file matching
`questions.<lang>*.json`.

### Media
A question may carry one media item: `image`, `audio`, or `video` (short, ≤15s). Stored on the
server's disk under `wwwroot/media/`, referenced by relative URL. `none` is the common case.

## Design language — "Persian tilework, modern game"

Not a template. The visual identity is **girih** — the geometric strapwork of Persian tile
mosaics — rendered as a modern game UI.

- **Ground**: deep lapis-to-midnight gradient (`#0B1030` → `#16215C`) with a tiled SVG girih
  lattice at very low opacity, plus a slow-drifting radial glow.
- **Accents**: firouzeh turquoise `#2EC4B6` (primary/correct), saffron `#F2B441` (score,
  highlights), pomegranate `#D6435B` (wrong/danger), parchment `#F3E9D8` (text).
- **Shapes**: cards are topped with a **pointed arch (taq)** clip; the shamseh (8-point star)
  is the app mark, the loading spinner, and the correct-answer burst.
- **Type**: Vazirmatn throughout — it carries both Persian and Latin, so one font keeps
  fa/en visually identical. Persian digits in fa, Latin in en.
- **Motion**: CSS/WAAPI only. Card deal-in, countdown ring drain, answer flip-reveal,
  score count-up, shamseh burst on a win.
- **RTL is first-class**, not a bolt-on: logical CSS properties throughout
  (`margin-inline`, `inset-inline`), `dir` flips with the language.

Every screen — game, auth, admin — uses the same tokens, the same card, the same buttons.

## Architecture

```
Browser (Blazor WASM PWA)  ──HTTPS/JSON──▶  ASP.NET Core host
                                              ├── Minimal API (JWT)
                                              ├── Orleans silo (co-hosted)
                                              │     PlayerGrain · MatchGrain
                                              │     MatchmakingGrain · QuestionBankGrain
                                              │     QuestionGeneratorGrain (daily reminder)
                                              ├── Redis  ← clustering, match state, leaderboard
                                              └── Mongo  ← users, questions, categories, history
```

> **Built differently from this plan, deliberately:** the AI provider is **OpenRouter**
> (OpenAI-compatible chat completions with a JSON schema), not the Anthropic SDK, so any model
> OpenRouter serves can be selected from configuration. Grain interfaces live in their own
> assembly (`Quesshi.Grains.Abstractions`) so a caller never references an implementation, and the
> Blazor project is `Quesshi.Web` — there is no separate Orleans host, the silo runs inside the
> ASP.NET app. All user-facing text is in JSON translation files; the code contains none.
> Also: the community Orleans MongoDB provider is
> still on Orleans 9 while the rest of the stack is on 10.2.2, so Mongo is reached through
> `MongoDB.Driver` in Infrastructure instead of as an Orleans storage provider. Redis carries
> clustering, grain state and reminders. `QuestionBankGrain` was dropped — Mongo's `$sample`
> already does random selection, and a caching grain in front of it was machinery with no job.
> Web push is not built; see the README.

### Projects (clean architecture)

```
src/
  Quesshi.Domain/          entities + rules, zero dependencies
  Quesshi.Application/     use cases + port interfaces (repos, otp, generator, clock)
  Quesshi.Infrastructure/  Mongo repos, Redis leaderboard, Claude generator, OTP senders
  Quesshi.Server/          host, Orleans grains, endpoints, auth, media upload
  Quesshi.Client/          Blazor WASM PWA (game + admin)
  Quesshi.Shared/          DTOs crossing the wire
tests/
  Quesshi.Domain.Tests/        scoring, match state machine
  Quesshi.Application.Tests/   use cases against in-memory fakes
  Quesshi.Server.Tests/        Orleans TestingHost + API integration
```

Dependency rule: Domain ← Application ← Infrastructure/Server. Domain references nothing.
No MediatR, no CQRS split, no repository-per-entity ceremony — use cases are plain injected
classes.

### Grains

| Grain | Key | State | Job |
|---|---|---|---|
| `PlayerGrain` | userId | Mongo | profile, friends, stats, streak, push subs |
| `MatchGrain` | matchId | Redis | question set, answers + timings, resolution, 48h forfeit |
| `MatchmakingGrain` | `0` | Redis | random-opponent queue |
| `QuestionGeneratorGrain` | `0` | Mongo | daily reminder → tops up thin category/level/lang buckets via the Claude API |

### Fairness (never simplify away)
- Correct answers never reach the client before that player has answered.
- Server stamps when each question was served; late submissions score zero. The client timer is decoration.
- The question set is fixed at match creation.

## Auth (passwordless)

- **Email OTP**: 6 digits, 10 minutes, 5 attempts, single use. Sender is a port —
  console sender in dev (code printed to the log and shown in the UI when `Auth:DevOtp=true`),
  SMTP in production.
- **Google OAuth** via OIDC. If no client id is configured the button is hidden — the app still runs.
- Either path issues the same JWT. First login creates the player and asks for a display name.
- **Guests**: an invite link (`/join/{code}`) offers signing in *or* typing a name and playing. A
  guest is a real player record with an unroutable `.invalid` address, marked `IsGuest`, and its
  token carries a `guest` claim. The game API allow-lists the four endpoints a guest may call and
  denies everything else by default, so an endpoint added later is closed to them until someone
  says otherwise; the client mirrors it with `PageBase.AllowsGuest`. Guests stay off the
  leaderboard and out of player search — a throwaway name should not be able to take a rank.

## Scheduled generation

`QuestionGeneratorGrain` holds a daily Orleans reminder. For every
`(lang, category, level)` bucket below a stock threshold it asks the Claude API for a batch,
validates the shape (4 choices, one correct, no duplicate text), and inserts as
`status: pending`. Nothing pending is ever served to players. The admin panel is where pending
becomes approved. No API key configured → the job logs and no-ops.

## Admin panel

`/admin`, gated on `IsAdmin`. Dashboard (counts, thin buckets), Questions (filter by
lang/category/level/status, approve/reject/edit/create, media upload), Categories (CRUD,
icon, colour), Users (list, promote, ban), Generation (run now, run history).

## Milestones

1. **Skeleton** — projects, compose.yml (podman: mongo+redis), Orleans silo, health check. *Verify:* silo starts, grain round-trips in TestingHost.
2. **Domain, test-first** — scoring, match state machine, OTP rules. *Verify:* `dotnet test` green with no infrastructure running.
3. **Persistence + seed** — Mongo repos, Redis leaderboard, ~150 hand-written bilingual questions. *Verify:* seeder is idempotent; bank grain serves only approved.
4. **Auth** — OTP + Google + JWT, first-login profile.
5. **Duel** — MatchGrain lifecycle, challenge link, forfeit reminder, matchmaking. *Verify:* TestingHost covers late answers, hidden opponent answers, forfeits.
6. **UI** — design tokens, girih ground, the full game flow in fa and en.
7. **Admin + generation** — admin panel, Claude generator, daily reminder.
8. **PWA + run** — manifest, service worker, then run it locally end to end.

All eight are done. 68 tests pass; the duel, admin panel and both languages were driven end to end
in a real browser.

## Deliberate shortcuts (marked `ponytail:` in code)
- Media on local disk, not object storage.
- One admin approving a queue; no community moderation.
- Single silo (clustering is real, so a second node is config).
- No SignalR — async duels do not need it.
- Web push deferred to after the first playable run; still outstanding.
- Media questions are supported end to end but none ship in the seed bank.
