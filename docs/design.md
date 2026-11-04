# Quesshi: design of the rebuild

Status: **draft for discussion**. Decisions marked *(decided)* were agreed on 2026-11-03; everything
else is a proposal. Open questions are collected in the last section.

## 1. Why a rebuild, and what "smaller" means

Production runs today's application (`src/`: Orleans, Redis, MongoDB, SignalR). A second
implementation (`platform/`: Orleans on PostgreSQL with row-level security, a plugin platform and a
Blazor WebAssembly client) reached about 94,000 lines in three weeks without being deployed. It
carried machinery the product does not need: tenancy, a plugin runtime, SQL in every layer, a
reentrant grain, a 2.8 MB client download, and about 760 tests that boot the whole host on
PostgreSQL.

This rebuild keeps the product and drops the machinery. Its goals:

- **Small.** One process, one database file, one language. Target: well under half the platform's
  code for the same product surface.
- **Contained features.** A feature lives in one folder; adding the folder and one line enables it,
  removing them removes it.
- **Live games for large audiences.** A host on a stage, hundreds or thousands of phones answering.
- **Fast feedback.** Most tests run without a database or a server; the whole suite in minutes.

Non-goals: tenancy, run-time plugins, several servers. Each can be added later without
rewriting the core (section 13).

## 2. Stack *(decided)*

| Concern | Choice |
| --- | --- |
| Runtime | One **Bun** process |
| HTTP and WebSocket | **Elysia**, pinned; imported only by route files |
| Database | **SQLite** (`bun:sqlite`, WAL), one file next to the media folder |
| Live state and caches | In the process's memory |
| Client | **Preact** single-page app, built separately, served by the same process |
| Live connection | **WebSocket** (Bun's publish/subscribe for rooms) |
| Everything else | JSON over HTTP; a typed client from the server's routes (Eden) |
| Not used | Redis, a separate database server, Orleans, server-side rendering frameworks |

## 3. Layout

```
quesshi/
  server/
    core/            # identity, sessions engine, questions, live hub, events, db, migrations runner
    app.ts           # builds the Elysia app from core + listed features
  client/            # shell, routing, auth screens, session screens, slot host
  features/          # one folder per feature, holding its server part, client part, kinds,
                     # formats and migrations; features/index.ts is the hand-written list
  tests/             # cross-cutting tests: architecture, HTTP, WebSocket, a few browser journeys
  docs/
```

A feature is one folder for both sides, so its client can use its kinds' card and answer types
without anything leaving the folder.

Rules, enforced by lint and a test:
- **`core` never imports `features`.**
- **A feature imports only `core`'s public API and `shared`, never another feature.** If two
  features need each other, they become one feature *(decided)*.
- **Elysia appears only in route files.** Rules, stores and engines are plain TypeScript.
- **SQL appears only in store files** (section 6).

## 4. Features *(decided)*

A feature is a folder with an `index.ts` that exports one `defineFeature(...)`. The app runs it at
start-up. It is enabled only when it appears in `server/features/index.ts` (and the client list);
there is no folder scanning.

```ts
export default defineFeature({
  name: "live-reactions",                                  // route prefix /f/live-reactions, topics live-reactions:*
  migrations: new URL("./migrations", import.meta.url),
  server: (core) => new Elysia({ prefix: "/f/live-reactions" })
    .ws("/:sessionId", { /* … */ }),
  on: { "session.finished": archiveReactions },            // core events, after commit
  kinds: [],                                               // question kinds it adds (section 7)
  formats: [],                                             // session formats it adds (section 7)
});
```

What a feature may contribute: routes and WebSocket endpoints under its prefix; its own tables and
migrations; handlers for core events; question kinds and session formats; client slots (places the
core's screens offer, such as the host screen's overlay).

Start-up rules:
- **Migrations:** core migrations first, then each listed feature's, recorded per feature.
- **Removing a feature keeps its tables and data.** They are reported as orphaned; an explicit
  command drops them.
- **Scope is local.** A feature may not register global hooks. Prefixes must be unique.
- **Fail fast.** A feature that fails to load (bad migration, duplicate prefix) stops start-up.
- **One database handle,** owned by the core and passed in. A feature never opens its own.

Example: *trivia* (its kinds and formats), *party games* (different kinds, formats and screens),
*live reactions* (a host-screen overlay and a WebSocket channel) are three features sharing only
the core.

## 5. Core

The core is what every feature relies on and nothing more:
- **Identity and access:** players, guests, admins, sign-in, cookies, protections (section 9).
- **Questions and categories:** storage, admin management, import, media.
- **The session engine:** lobbies, seats, the clock, the kind and format contracts, settlement,
  history.
- **The live hub:** WebSocket rooms, the session actor, batched writes (section 8).
- **Events:** an in-process bus. Handlers run after the change has committed; a failing handler is
  logged and retried, and never undoes the change.
- **Jobs:** a small `jobs` table and an in-process worker for mail and deferred work.

## 6. Data

**Database.** One SQLite file in WAL mode: any number of readers, one writer at a time. The core
opens it once.

**Stores.** All SQL lives in store files (`*.store.ts`), one per area, with methods named in the
domain's language (`loadJoinFacts`, `seat`, `saveAnswers`). Store methods are `async` even though
`bun:sqlite` is synchronous, so a move to PostgreSQL changes no caller.

**Writes never overwrite each other** *(decided)*:
- A transaction is a synchronous function, `db.transaction(() => { read; decide; write })`. Nothing
  else runs between its read and its write. **No `await` inside a transaction.**
- Counters and toggles are single statements (`set n = n + 1`, upserts).
- A live session is changed only by its session actor (section 8).
- Records people edit (questions, categories, profiles) carry a `version`. A save names the version
  it started from; if someone saved in between, nothing is written and the user is told to reload.

**Portability to PostgreSQL** (should several servers ever be needed): store files only; async
store methods; SQL both databases share (`ON CONFLICT`, `RETURNING`, CTEs, window functions,
`->`/`->>` for JSON); timestamps written by the application as ISO-8601 text; ids as text UUIDs;
store tests run the real database. Estimated move: three to five days.

**Core tables (sketch).**

```sql
questions(id, category_id, kind, kind_version, prompt, payload json, attributes json,
          lang, status, version, created_at, updated_at)
categories(id, slug, names json, version, …)
players(id, display_name, avatar, lang, email null, kind 'guest'|'member', banned_at, deleted_at, …)
admins(id, username, password_hash, role, active, …)
auth_sessions(token_hash, player_id null, admin_id null, expires_at, …)
sessions(id, code, format, status, rules json, owner_id, version, created_at, ended_at, …)
seats(session_id, player_id, role, joined_at, …)
answers(session_id, player_id, slot, value json, correct, points, answered_at,
        primary key (session_id, player_id, slot))
jobs(id, kind, payload json, run_at, attempts, locked_until, …)
```

**Extending questions:**
- **A new question type** is a new `kind` value with its own `payload` schema, in the feature that
  owns it. No migration.
- **A small extra field** goes into `attributes`; it becomes a real column with an index once it is
  queried or sorted on.
- **A feature's data about questions** goes into that feature's own table, keyed by `question_id`.
  The core table is never changed for a feature.

## 7. Kinds and formats

**Kind:** the shape of one question. It provides a payload schema (validated on save and on
import), the player's answer schema, grading (pure), and its client components (play and reveal).

**Format:** how a session runs. It decides who acts when, the clock, scoring and the end, as a
pure state machine: `next(state, event, now) → { state, effects }`. Effects are what the engine
does afterwards: write rows, broadcast a view, set a timer. Formats never touch the database, the
network or the clock directly, so they are fully unit-tested.

A format declares which kinds it accepts (a scored format accepts gradable kinds; a poll accepts
ungraded ones).

**Kinds and formats belong to features** *(decided)*. A feature owns its question types, how they
are graded and scored, the formats that play them, and their screens. The core only defines the
kind and format contracts and runs them. Removing a feature removes its kinds and formats.

**First release** *(decided scope)*: two features.

| Feature | Kinds | Formats |
| --- | --- | --- |
| `trivia` | `choice`, `order`, `map_pick` | `live`, `async_duel` |
| `polls` | `poll_choice`, `poll_roster` | `poll` |

**Ported from the platform:** the rules of these kinds and formats, with their unit tests as the
specification. `flashcard` and `flashcards` come later, with the other features.

## 8. Live sessions

**One session actor per running session,** an object in memory that owns that session's state:
- It receives commands (join, start, answer, next) one at a time from a queue, so no two commands
  change the session at once.
- It applies the format's pure `next`, then performs the effects.
- **Answers** are graded and scored in memory and acknowledged at once. They are written to SQLite
  in batches (every ~100 ms, or sooner at a size limit) in one transaction; the batch writer sits
  behind a small interface so it could later be replaced by a queue.
- **Timers** (question deadlines, grace periods) belong to the actor and use an injected clock.
- **After a restart,** an actor is rebuilt from the database on first use.

**Connections:**
- Each phone opens one WebSocket to `/live/:code`. It subscribes to the session's topic; the actor
  publishes each new view once to the topic (`server.publish`). Personal parts (your score, your
  answer) are sent only to that socket.
- **Views, not events:** every message carries the session version; a client drops anything older
  and asks for the full view after a reconnect.
- **The host screen** is a client with the host role on the same session.
- **Scale target** (to confirm with a benchmark early): one session with 1,000 phones answering
  within two seconds; acknowledgement under 100 ms.
- **Fallback** for networks that block WebSockets: server-sent events plus HTTP POST, behind the
  same live-channel interface. Built only if needed.

**Asynchronous formats** (such as `async_duel`) use ordinary HTTP commands; the same actor model
applies while a session is active.

## 9. Identity and access *(behaviour kept from today, without tenancy)*

- **Players** sign in with a six-digit emailed code (valid ten minutes, five attempts, stored as an
  HMAC, verified in one statement) or with Google. **Guests** play without an account and can
  upgrade with an email; results earned as a guest stay unranked.
- **Admins** sign in with a username and password, with roles, lock-out, reset tokens and
  activation.
- **Sessions are server-side rows** referenced by an `HttpOnly`, `Secure`, `SameSite=Lax` cookie;
  players and admins use different cookies. A session can be revoked at once (bans, deletion).
- **Protections:** an antiforgery token and an `Origin` check on every change; guests denied by
  default except join, play, own history, own profile and upgrade; rate limits on code requests and
  admin sign-in; an append-only admin audit log.
- **Deleting an account** scrubs the profile and asks each feature to clean up its data through an
  event, retried until done.

Without tenancy, today's split between global accounts and per-tenant players collapses into one
`players` table.

## 10. Client

- **Preact** with a small router, a typed API client from the server (Eden), and the WebSocket
  live channel.
- **Landing and join-by-code pages** are rendered on the server (`preact-render-to-string`) so a
  phone shows something at once; the app takes over after load.
- **Each feature's client part** is its own lazily loaded chunk, listed by hand like the server
  list. Slots in the core screens render the parts of enabled features.
- **Languages:** English, Persian and Dutch, with right-to-left layout.
- **Target size:** the first screen interactive with well under 200 KB compressed.

## 11. Testing

- **Rules first:** kinds, formats, scoring and access rules are pure functions, tested without a
  database or server. This is most of the suite.
- **Stores:** each store method is tested against a real SQLite file in a temporary directory.
- **HTTP and WebSocket:** tests drive the real app in process (Elysia's `handle`, and a real socket
  for live sessions) with a fake clock and fake mail.
- **Browser:** a handful of journeys (join by code and answer, host a live session, admin creates a
  question), each with an explicit timeout.
- **No real waiting:** time comes from an injected clock; a test advances it. `setTimeout` waits
  in tests are refused by a lint rule.
- **Goal:** the whole suite in under five minutes on CI, once per pull request.

## 12. Operations

- **One process** with a data directory: the SQLite file and media.
- **Backups:** a nightly `VACUUM INTO` copy, optionally Litestream for continuous replication.
- **Deployment:** on the VPS behind the existing reverse proxy; configuration through environment
  variables; mail through the existing SMTP setting.
- **Moving data from today's application:** questions, categories and media from MongoDB through an
  export script (today's bank export is the starting point). Whether players and history move is an
  open question.

## 13. Growing later (not built now)

| If… | Then |
| --- | --- |
| Several servers are needed | Move to PostgreSQL (section 6); route each live session to the server that owns it; broadcast across servers through Redis or an MQTT broker behind the live-channel interface |
| An event reaches tens of thousands of phones | Put an MQTT broker (with WebSocket support) in front of the fan-out |
| Editions (adults, students, education) | Separate category sets and configuration first; full tenancy only if isolation is really needed |
| Features switched per user, region or time | An `enabled(ctx)` check in the feature contract |

## 14. Backlog carried from the closed issues

- #145 a new question or duel type
- #135 category prompt helpers and per-run generation instructions
- #128 more fun questions and answers
- #105 admin: fix reported questions in place, show topic, record question provenance
- #85 reveals: carry the tolerance radius and the asynchronous history verdict

## 14a. As built (first release, 2026-11-03)

Where the first release differs from the plan above, the code is the reference:

- **No Eden client.** The client calls the API with a small `fetch` helper (`client/api.ts`); the
  app's route types are too loose after mounting features in a loop for Eden to add much.
- **No event bus yet.** Nothing needed one: no feature reacts to another's change. Add it with the
  first handler that does.
- **Answers are written as they arrive,** in the actor's synchronous transaction, not batched. The
  500-player benchmark (`scripts/bench-live.ts`) showed the cost was in the broadcasts, not the
  writes: broadcasts are now batched per event-loop turn and kept small (`broadcastView`), and two
  500-player games use about 100 MB.
- **No server-sent-events fallback.** WebSockets work through the Cloudflare tunnel in production.
- **Games survive restarts** through JSON room snapshots (`room_snapshots`): each running room saves
  its state with every broadcast and its room type's `resume()` rebuilds it at start-up.
- **The architecture rules are enforced by `tests/architecture.test.ts`,** not a lint rule.
- **Releases:** every merge to `main` is tested, versioned by date and deployed as one image (see
  `docs/operations.md`).

## 15. Open questions

*Answered 2026-11-03:* the first release is the core, sign-in, admin for questions and
categories, and the `trivia` and `polls` features (section 7); stats, leaderboard, friends,
matchmaking, reports, AI generation, private question sets (which replace "packs") and flashcards
come later. Kinds and formats belong to
features.

1. ~~**Data from today's application.**~~ *Answered 2026-11-03:* questions, categories, content
   settings and media only (`scripts/import-legacy.ts`); players and history start fresh.
2. ~~**Folder name.**~~ *Done:* the rebuild replaced the repository root.
