# Quesshi platform architecture

This is the target design for refactoring Quesshi into a multi-tenant quiz platform that grows by
plugins. It keeps the stack Quesshi has: .NET 10, ASP.NET Core, Orleans and Blazor WebAssembly.
What changes is the storage (PostgreSQL only), the shape of the game code (one session engine with
pluggable formats and question kinds), the delivery channel (server-sent events and plain POST) and
tenancy (tenants and their features as data).

Nothing here is built yet. The data model is in [schema.sql](schema.sql). Section 19 says how the
existing code maps onto this design and in what order to get there: the new platform is built
beside the old code in this repository, and production moves to it in one cut-over. Review records are in
[review/](review/).

Most of this design was first written and reviewed for a rebuild on another stack. The parts that
did not depend on the stack (the schema, the kind and format contracts, events, row security,
endings, delivery rules) are carried over. The parts that did are rewritten here for Orleans.

## 1. Goals and non-goals

**Goals**

- **Keep what works.** .NET, Orleans, Blazor, the scoring, shuffle, map and duplicate-detection
  rules, the admin panel, sign-in mail, AI generation and the deployment pipeline.
- **One database.** PostgreSQL for all durable state. Redis and MongoDB are removed.
- **The system grows by plugins.** A question kind, a game format, a feature, a sign-in method or
  a content source is one folder. Adding one does not edit the core.
- **A small fixed core.** Tenant isolation, identity, the session engine, delivery and the job
  runner are not pluggable, because they enforce the rules plugins rely on.
- **A game result never waits for a feature.** Finishing a session is core work only. Everything a
  feature derives from it (stats, boards) follows within seconds, exactly once.
- **Tenants are data.** Creating a tenant or toggling a plugin for it is a row change, not a deploy.
- **Grains write, the database is read.** A session's grain is its only writer and holds its
  working state in memory. Every read that is not part of a command (views, history, lists) comes
  from PostgreSQL and never wakes a grain.

**Non-goals**

- Plugins loaded at run time or supplied by third parties. Plugins are compiled in and listed in
  one registry; they are trusted code.
- Orleans grain persistence and Orleans reminders in the new platform. Neither is used (7.4, 7.5).
- Changing how the game plays. Where this document and today's rules differ, today's rules win
  unless the difference is listed in 19.4.
- Playing offline. The shell and assets are cached; play needs the network.
- A separate database or schema per tenant.
- Sharing rows between tenants. Content moves between tenants by export and import, as copies.
- Adaptive questions whose card depends on earlier answers (6.7).
- Guaranteed delivery of every intermediate screen. A slow or reconnecting client sees the current
  state and may skip a phase (9.2).

## 2. The concepts

| Concept | What it decides | First ones |
|---|---|---|
| **Kind** (plugin) | The shape of one question: what the player sees, what they send back, how it is graded | `choice`, `order`, `map_pick`, `poll_choice`, `poll_roster`, `flashcard` |
| **Format** (plugin) | How a session runs: who acts when, the clock, the scoring, when it ends | `async_duel`, `live`, `poll`, `flashcards` |
| **Feature** (plugin) | A capability outside any one session, with its own routes, tables and screens | `stats`, `leaderboard`, `friends`, `matchmaking`, `reports`, `ai_generation` |
| **Sign-in method** (plugin) | A way to prove an external identity | `google` |
| **Content source** (plugin) | Where a session's questions come from | `bank`, `pack` |
| **Core switch** | A per-tenant on/off for behaviour that lives in the core | `guests`, `custom_questions` |
| **Tenant** | Hosts, branding, languages, and which of the above are on | `quesshi`, `quessher` |

Names against today's code: `order` is today's Sort, `map_pick` is Map, `poll_choice` and
`poll_roster` are Voting's fixed-choice and participant-roster questions, `async_duel` is the
async duel (`Match`), `live` is the live duel (`LiveMatch`), `poll` is Voting (`VotingMatch`).
`flashcards` is new.

A format declares which kinds it accepts. A scored format accepts only gradable kinds; `poll`
accepts only ungraded ones. That is the only coupling between kinds and formats.

Not everything a tenant can toggle is a plugin. `guests` and `custom_questions` have no tables or
reactions of their own; they are switches the core checks inside its own identity and content
code, so turning one off really turns the behaviour off.

## 3. Stack

| Layer | Choice | Notes |
|---|---|---|
| Runtime | .NET 10, ASP.NET Core minimal APIs | As today |
| Game runtime | Orleans 10, silo co-hosted in the web host | One grain type, for sessions |
| Cluster membership | Orleans ADO.NET provider on PostgreSQL | Replaces Redis clustering |
| Database | PostgreSQL 18, Npgsql, Dapper | SQL is written by hand. No EF Core, no Orleans storage provider |
| Migrations | Plain SQL files in one globally ordered stream | Forward-only; applied by `quesshi migrate` under its own database role |
| Client | Blazor WebAssembly (PWA) | As today |
| Push | Server-sent events | Replaces both SignalR hubs. Commands go over plain `POST` |
| JSON | `System.Text.Json` | Plugin payloads are C# records; JSON Schemas for AI output are exported from them |
| Styling | The existing hand-written CSS, with the tenant theme as CSS variables | |

Removed at the cut-over: MongoDB, Redis, SignalR, the Redis SignalR backplane, Orleans Redis
persistence and reminders.

**Database conventions, stated once.** Ids are `Guid` in code and `uuid` in the database. JSON
columns are passed as typed `jsonb` parameters, never as plain strings, and read back through
`System.Text.Json` with source-generated metadata. Lists are passed as arrays and matched with
`= any(@ids)`. Composite keys are ordinary column predicates. Every statement in a tenant
transaction uses that transaction's connection.

**Connection budget per silo.** The application data source is capped at 40 connections and the
system one at 10, including the listening connection. Inside that: view rendering uses at most 8
at a time and releases its read transaction before writing to any stream; the job worker uses at
most 4. PostgreSQL's connection limit is sized for the number of silos times 50, plus headroom
for migrations and an operator.

## 4. Solution layout

```
src/
  Quesshi.Shared/              shared by server and client: DTOs, views, cards, commands, and the
                               kind contract (the only plugin contract the client needs)
  Quesshi.Domain/              pure rules with no dependencies: scoring, positions, standings
  Quesshi.Kinds/               one folder per kind: pure logic and records. References Shared only.
                               Used by the server and by the Blazor client
  Quesshi.Engine.Contracts/    the format contract and the in-memory session handle. Pure types only
  Quesshi.Formats/             one folder per format: pure logic. References Engine.Contracts,
                               Domain and Shared, and nothing that can reach a database
  Quesshi.Plugin.Api/          what a feature, source or sign-in plugin may see of the core, as
                               interfaces: ITenantTx, ITenantDb, ISessions, IContent, ILocks, IInbox,
                               and the records named queries return
  Quesshi.Features/            one folder per feature, sign-in method and content source.
                               References Plugin.Api only
  Quesshi.Application/         the core: tenancy, identity, content, session engine, events, jobs.
                               Implements Plugin.Api; its own types are internal
  Quesshi.Infrastructure/      PostgreSQL access, mail, storage, model provider, clock
  Quesshi.Grains.Abstractions/ grain interface and its serialisable command and result types
  Quesshi.Grains/              SessionGrain
  Quesshi.Server/              host: API, streams, silo, registry, landing page, migrate command
  Quesshi.Web/                 Blazor shell: routing, auth, lobby, profile
  Quesshi.Web.Plugins/         Razor class library: the client half of kinds, formats and features
  Quesshi.Web.Admin/           Razor class library: the admin area. Lazy-loaded
  Quesshi.Kinds.MapPick.Client/  the map kind's components and the world map. Lazy-loaded
tests/
  ...Domain, Kinds, Formats, Application, Features, Server (grains and database), Web
```

How the rules are actually enforced, and where enforcement stops:

- **By project references:** `Features` reference `Plugin.Api`, not `Application`, so they cannot
  reach core internals; `Application`'s types are internal, visible only to `Infrastructure`,
  `Grains` and `Server`. `Formats` reference `Engine.Contracts`, which contains no database
  interface at all, so a format cannot even name a transaction. `Kinds` references only `Shared`.
  The client references `Shared`, `Kinds` and the client libraries.
- **By a banned-API analyzer** on `Kinds` and `Formats`: database, network and clock types,
  `Random.Shared`, `DateTime.Now`, `Guid.NewGuid` and reflection emit a compile error.
- **By a test over compiled references:** no plugin folder's types reference another plugin
  folder's namespace.
- **Not enforced:** a plugin could still misbehave through a helper in `Shared`, or by doing
  something slow. Those are caught in review, not by tooling.

During the build these projects live in their own solution under `platform/`, beside today's
`src/` (19.2). The names above are the final ones.

Migration files sit next to their owner (`Application/Migrations/`,
`Features/<id>/Migrations/`) but are named with a timestamp prefix and run as one ordered stream.

## 5. Plugins

### 5.1 What is and is not a plugin

| Plugin types | Fixed core |
|---|---|
| Question kinds | Tenant isolation and the database roles |
| Game formats | Accounts, players, guests, sign-in sessions |
| Features | The session engine: the grain, transactions, clock, settlement |
| Sign-in methods | Delivery: streams, views, inbox |
| Content sources | Durable events and the job runner |

Mail, file storage and notification delivery each have one plain implementation behind a small
interface. They become swappable when a second implementation is actually needed.

### 5.2 A plugin's two halves

Every plugin has a **server definition** and, if it has screens, a **client definition** with the
same id.

- The server registers each plugin with one explicit line in `Server/Registry.cs`.
- The client registry knows, for each plugin id, which assembly holds its client half. Eager
  assemblies register their components at start-up. A lazy assembly is loaded with
  `LazyAssemblyLoader` when first needed, and then its own entry class registers its components
  using ordinary `typeof` references. Those references are what keep the components and their
  parameters from being trimmed; the eager registry never names a type from a lazy assembly.
  Components are rendered with `DynamicComponent`. The published-build test renders every
  registered component once.
- **Enabled means "offered for new play".** A kind or format turned off for a tenant is no longer
  offered or drawn, but sessions that already use it keep playing and their components stay
  loadable until they end. Ending those sessions early is a separate, explicit admin action (7.5).
- The shell receives the tenant's enabled plugin ids and a `configVersion`. Every pushed view
  carries the current `configVersion`; when it changes, the client re-reads the tenant
  configuration, so a plugin turned off disappears from an already-open browser.

### 5.3 What a feature plugin can declare

Kinds, formats, sign-in methods and sources have their own narrow contracts (sections 6, 7.2, 8,
11.2). A feature plugin is the general form. It can declare six things and nothing else. Each is
built only when its first real consumer is: routes, events, clean-up and jobs with `stats` and
`leaderboard`; queries with the friends board; inbox items and schedules with `matchmaking`.

```csharp
public sealed class LeaderboardFeature : IFeature
{
    public string Id => "leaderboard";
    public Type Settings => typeof(LeaderboardSettings);     // record with defaults and validation

    public void Configure(IFeatureBuilder f)
    {
        // 1. Routes. The plugin never receives the application's route builder. It fills in
        //    three core-built groups; the core applies tenant resolution, the capability check
        //    and the right sign-in to every route in each group.
        f.PlayerRoutes(r => r.MapGet("/{board}", GetBoard));              // /api/x/leaderboard; guests refused
                                                                          // unless a route says .AllowGuests()
        f.AdminRoutes(AdminRole.Editor, r => r.MapGet("/boards", ListBoards));
        f.OpenRoutes(r => { });                                           // no sign-in; rarely used

        // 2. Event handlers. Each runs later, in its own tenant transaction, exactly once (5.5).
        f.On<SessionEnded>(ApplySessionEnded);
        f.On<PlayerUpgraded>(ApplyPlayerUpgraded);

        // 3. Clean-up. Mandatory for any plugin that stores rows about a player.
        //    Runs whether or not the plugin is enabled for the tenant.
        f.CleanupPlayer(RemovePlayer);

        // 4. Jobs and schedules, for work that leaves the database or takes long.
        f.Job("rebuild", Rebuild, whenDisabled: WhenDisabled.Cancel);
        f.Every(TimeSpan.FromMinutes(1), "sweep");

        // 5. Read-only queries offered to other plugins, and queries it would like to use.
        f.Provides<PlayerRank>("leaderboard.rank", GetRank);
        f.Uses("friends.ids");

        // 6. Items this plugin adds to a player's inbox.
        f.Inbox(GetInboxItems);
    }
}
```

```csharp
public sealed class LeaderboardClient : IFeatureClient
{
    public string Id => "leaderboard";
    public void Configure(IFeatureClientBuilder c)
    {
        c.Page("/ranks", typeof(Ranks));
        c.Slot(Slots.HomeTile, typeof(HomeTile));
        c.Slot(Slots.NavItem, new NavItem("nav.ranks", "/ranks"));
        c.Slot(Slots.ProfileSection, typeof(ProfileRanks));
        c.Slot(Slots.AdminPage, typeof(AdminBoards));
        c.Slot(Slots.SessionResult, typeof(ResultRank));     // parameter: the finished session view
    }
}
```

**Client slots.** Each slot has a typed set of parameters defined in `Shared`. Contributions are
shown in registry order. Page paths are checked for collisions by a test. The shell wraps every
contribution in its own `ErrorBoundary`, so a plugin that throws leaves a gap, not a broken screen.

**Deliberately not offered yet:** a callback inside a session's command, and a reaction to each
recorded answer. No first feature needs either; both would put feature code on the path of a live
answer. They are added when a concrete plugin (spaced repetition) needs them, with their rules
decided then.

### 5.4 Rules for plugins

- **Tenant-scoped by construction.** A plugin receives a transaction already bound to the tenant
  (`ITenantTx`), or an `ITenantDb` that opens one. It is never given a connection, a data source
  or a grain factory.
- **A plugin owns its tables.** They are created in the plugin's own migration files, start with
  `tenant_id`, and are registered through `quesshi_tenant_table()`, which checks the key, applies
  the standard policy and grants, and records the owner. A plugin's tables may reference core
  tables. They never reference another plugin's tables, and core tables never reference theirs.
- **Plugins never reference each other.** One plugin reads another's data only through a named,
  typed, read-only query the other provides. A query runs in the caller's transaction. If the
  providing plugin is off for the tenant, the query is unavailable and the caller must cope: with
  `friends` off, the friends board is simply not offered.
- **Changing core data goes through the core API.** `matchmaking` creates a session with
  `ISessions.CreateAsync`; `reports` returns a question to review with `IContent.SetStatusAsync`.
- **Features do not own grains.** Only the core's session grain exists. A feature that needs one
  writer for shared state uses the locking protocol below.

**Locking protocol for database transactions.** A session is serialised by its grain, not by a
database lock, so session commands are not part of this. The protocol covers the transactions that
*do* contend in the database: event handlers, clean-up, reports, and pack access.

- Shared rows are locked only through explicit core calls, and always in this order, ascending by
  id inside each step:

  1. `ILocks.PlayersAsync(tx, ids)`
  2. `ILocks.ContentAsync(tx, ids)` for questions and packs
  3. the plugin's own rows

- Asking again for a lock the transaction already holds is always allowed. Inserting new rows is
  always allowed.
- Player locks use the mode that does not conflict with foreign-key checks
  (`for no key update`), so a transaction that merely inserts a row referring to a player never
  waits on one.
- Inbox bumps (9.5) are collected by the transaction wrapper and applied last, in ascending
  player id.
- The transaction wrapper remembers the last explicit step taken and throws, in development and
  tests, when code asks for an earlier one. It checks these declared calls only.
- Deadlocks and serialisation failures that still occur are retried with backoff, up to three
  times.

| Transaction | Steps |
|---|---|
| A session command (inside the grain) | none; it writes only the session's own rows, guarded by `version` (7.4) |
| An event handler in `stats` or `leaderboard` | players, then its own rows |
| `friends` adding or removing | players (both), then its own rows |
| `matchmaking` pairing | players (both), then its own rows, then a new session (an insert) |
| `reports` | content (the question), then its own rows, then `SetStatus` on the question it holds |
| Deleting or purging a player | players, then each plugin's clean-up of its own rows |
| Drawing from a pack at session start | content (the pack) |

### 5.5 Events: how features react to the game

The core emits a small set of **durable events**:

| Event | When | Identifies |
|---|---|---|
| `SessionEnded` | A session reaches any terminal state | The session |
| `PlayerUpgraded` | A guest's player row gains an account | The player |

An event carries identifiers only. A handler loads the authoritative rows (the session, its
seats, their results, upgraded to the current shape by the format's readers), so a job queued
before a release still means the same thing after it.

How an event reaches a plugin:

1. In the transaction that causes the event, the core adds one job per plugin that handles it and
   is **enabled for that tenant at that moment**. Off means no event. The job's identity is
   (tenant, plugin, event type, subject id).
2. A worker runs the handler in one tenant transaction. Its first statement is
   `insert into plugin_effects ... on conflict do nothing returning`. Only if a row came back does
   the handler run; the marker and the handler's writes commit together.
3. In that same transaction the core notifies the session's streams that this plugin's effects
   are in.

That gives each plugin **exactly-once effects** even though jobs are delivered at least once: a
retried job, or two workers overlapping after a lease expires, cannot apply an event twice.
Markers are kept for good.

Rules that follow:

- **Events are unordered,** so every handler must give the same final result whatever order
  events are applied in. The two first plugins do it like this:
  - `leaderboard` writes every change to a ledger and derives the score by folding the ledger in
    time order (13). An entry that arrives in order is applied directly; one that arrives late
    triggers a refold for that player.
  - `stats` records one row per player per session, then updates sums by addition. Streaks are
    the one order-dependent figure: after locking the player, an event that is the newest for that
    player updates the streak directly, and a late-arriving older one triggers a recomputation
    from the plugin's own result rows, ordered by end time and session id.
- **Handlers serialise per player.** A handler locks the players it will write for, first, in
  ascending id. Clean-up takes the same locks. A handler skips any player who has been deleted
  (and still commits its marker), so a queued event can never recreate removed data.
- **A handler may not call the network or a grain.** It only reads and writes the database. Work
  that leaves the database (push, mail, a model call) is a job the handler enqueues.
- **A failing handler delays only itself.** The game result is already committed. The job is
  retried with backoff and finally parked as `dead`; an admin can re-run a dead job once the
  cause is fixed, and the marker makes that safe.
- **Turning a plugin off.** Events already queued while it was on are still completed, because
  they belong to the period it was on. A job declares what happens when its plugin is off at run
  time: `Complete` (the default for event handlers) or `Cancel` (for example, an AI generation
  run), which ends the job as `cancelled` without running it. Turning a plugin on later does not
  back-fill what happened while it was off, unless the plugin offers a rebuild job.
- **Removing a player's data is not an event and cannot be switched off.** A plugin's player
  clean-up runs for every installed plugin, enabled or not, whenever an account is deleted or a
  guest is purged (11.1).

**What the result screen shows meanwhile.** Places, scores and outcomes come from the core and
are there at once. For anything a plugin adds (a new rank, a streak), the client asks
`GET /api/sessions/{id}/effects`, which reports each plugin as `done`, `pending`, `off` or
`failed` (its job is dead) for that session. A plugin's contribution to the result screen shows a
pending state until its effects are `done`. The stream's 2-second reconcile (9.3) re-reads effects
for as long as a viewer is on a finished session with something pending, so the screen always
converges. Every change to the state of a session's effects (a handler finishing, its job dying, a
dead job being re-run) raises one counter for that session, `session_effects.revision`, in the
same transaction. A report carries that revision, read with the statuses, and the client keeps
the highest.

**What this costs:** a leaderboard or a stats page can be a second or two behind the result
screen. The result itself (places, scores, outcomes) is never behind.

This replaces today's settlement, where `MatchGrain` and `LiveMatchSettlement` call `PlayerGrain`
per participant with checkpoints, a dedup list on the player document and a retry reminder.

### 5.6 Adding, changing and removing a plugin

- **Adding:** `quesshi sync` creates the plugin's capability row for every tenant, not entitled.
- **Migrations:** one stream ordered by timestamp, so a plugin migration that needs a newer core
  column simply sorts after the core migration that adds it. `quesshi migrate` holds an advisory
  lock for the whole run, records each file's checksum, and refuses to run a new file whose name
  sorts before one already applied; such a file is renamed before release. Applied files are
  never deleted from the repository.
- **Removing a feature** takes two releases, so the previous build is never left running against
  missing tables. First release: the plugin is marked retired. It registers no routes or schedules and
  subscribes to no new events, but it still runs the jobs already queued for it, and keeps its
  readers, its clean-up and its tables. When every tenant has it
  off and its queue is empty, the second release removes the code and drops the tables together.
- **Kinds and formats are never removed** once they have produced finished sessions, only retired
  from new play, because history still needs their readers (6.3).

### 5.7 What "trusted" means

Plugins are our own code, compiled in. The rules above are kept by project references,
architecture tests and the database (row security, grants), not by a sandbox. Row security still
holds if a plugin has a bug; nothing stops a plugin from writing bad data inside its own tenant.

## 6. Question kinds

### 6.1 Storage

A question is a common envelope plus a kind-specific JSON payload (table `questions`).

- **Envelope:** what every kind shares and what the database filters on: kind, family, language,
  category, level, prompt, explanation, topic, status, source, owner. Media joins the envelope
  with the later media migration.
- **Payload:** everything else. Only the kind's own code reads it. It is always a JSON object.
- **`kind_version`:** the version of the kind that wrote the row.

This replaces today's single `Question` class, which holds the union of every kind's fields
(`Choices`, `CorrectIndex`, `Target`, `BaseLayer`) with a table of "must be null for this kind"
rules, and `QuestionDoc`, which has a nullable column per kind-specific field.

### 6.2 Server contract

```csharp
public interface IKind
{
    string Id { get; }
    int Version { get; }
    string Family { get; }        // duplicate control is per family: "trivia", "poll", "card"
    bool Gradable { get; }        // whether Grade ever returns credit
}

// TPayload etc. are records in the kind's folder. The engine handles them as JSON; the typed
// base class does the (de)serialisation and validates shapes.
public abstract class Kind<TPayload, TResolved, TCard, TAnswer, TStored, TReveal> : IKind
{
    // Converts a payload written by an older version. Used when authoring and drawing.
    public abstract TPayload Upgrade(int from, JsonElement payload);

    // Lazily converts what an old version stored in a session, when history is read.
    public abstract Snapshot<TCard, TReveal, TStored> UpgradeSnapshot(int from, RawSnapshot old);

    // Checks beyond the shape. Runs for admin forms, imports and AI output.
    // env: language, and the attached media's kind, size and duration if any.
    public abstract IReadOnlyList<Problem> Validate(TPayload payload, ValidateEnv env);

    // Runs once per item when the session starts. Whatever is random or depends on who is
    // playing is decided here: a shuffle, the frozen list of roster options.
    public abstract TResolved Resolve(TPayload payload, ResolveContext ctx);   // ctx: Rng, Roster

    public abstract TCard Card(Item<TPayload, TResolved> item);       // must not depend on the answer
    public abstract TReveal Reveal(Item<TPayload, TResolved> item);   // the answer side

    // Turns a submitted answer into its stored form. Throws MalformedAnswer for input no client
    // could have produced; the engine refuses it.
    public abstract TStored Normalize(Item<TPayload, TResolved> item, TAnswer answer);

    // Grade.None for kinds with no right answer, else Grade.Of(credit) with credit in [0, 1].
    public abstract Grade Grade(Item<TPayload, TResolved> item, TStored stored);
}
```

**How the engine calls a kind.** The engine does not know a kind's record types. The generic base
class implements a non-generic `IKindRuntime` whose methods take and return `JsonElement`, and
does the conversion with the kind's source-generated `JsonSerializerContext`, so nothing depends
on run-time reflection and the client can be trimmed. The registry is keyed by `(id, version)`:
grading a running session's item always uses the implementation for the version it was drawn with.

Optional interfaces on the same class: `IGenerates` and `IImports`. `IGenerates` supplies a prompt
fragment, a **candidate** record with its JSON Schema, post-checks, and the conversion from a
candidate to a payload. The candidate is separate from the payload because checks can need facts
the stored question does not keep: a generated city question names its country so the coordinates
can be checked against that country's outline, as today, and the country is then dropped.

At session start the engine calls `Resolve`, `Card` and `Reveal` once for each item and stores all
three with the snapshot. After that, showing a card or a reveal is reading a row.

What moves into kinds from today's code: `SubmittedAnswer.TryGrade` and
`Question.IsCorrect/ServedChoices/Validate` split by kind; `SortOrder` into `order`; `MapTarget`,
`Geo` and the country check into `map_pick`; the per-kind generator schemas (`SortSchema`,
`MapSchema`, `QuestionSchema`, `VotingQuestionSchema`) into each kind's `IGenerates`.

### 6.3 Rules every kind follows

- **The card does not depend on the answer.** Tested as non-interference: two payloads that differ
  only in their answer must produce identical cards for the same resolved value. For `order` this
  means the displayed arrangement is derived from the items themselves and the random source, not
  by permuting the stored (correct) order.
- **Everything session-specific is decided once and stored.** Resolved value, card and reveal are
  written at start. A reconnect, a second silo or a later release cannot show one arrangement and
  grade against another. (Today the shuffle is recomputed from `(matchId, slot)` in three card
  builders and two submission paths.)
- **Two lifetimes of compatibility.**
  - *While a session runs,* it is played with the version it was drawn with: that version's
    grading on the server and that version's `Play` and `Reveal` components and answer shape in
    the client. Because no session lives longer than 90 days (7.1), a kind keeps each version's
    play code for 90 days after it stops being drawn. `UpgradeSnapshot` is never applied to a
    running session.
  - *After a session has ended,* only readers are needed. `UpgradeSnapshot` converts what an old
    version stored (card, reveal, stored answers) when history is read. These small pure steps are
    kept for ever. Finished sessions are never rewritten, and their recorded credit and points are
    never recomputed.
- **Answers are normalised once.** Grading, reveals and history read the stored form.
- **Malformed is not wrong.** Malformed input is refused; a wrong answer is stored and graded.
- **Numbers are JSON numbers,** read and written with the invariant culture.
- **Credit is a fraction.** All-or-nothing kinds return 0 or 1.
- **Behaviour that could drift is pinned by the version.** Fuzzy text matching, Unicode
  normalisation and hotspot tolerances are fixed per `kind_version` and covered by fixtures.
- **Kinds are pure** (section 4).

### 6.4 Client contract

```csharp
public sealed class OrderKindClient : IKindClient
{
    public string Id => "order";
    public Type Play(int version)   => typeof(OrderPlay);     // parameters: Card, OnAnswer, Disabled
    public Type Reveal(int version) => typeof(OrderReveal);   // parameters: Card, Reveal, Answers
    public Type Editor => typeof(OrderEditor);                // parameters: Payload, PayloadChanged, Lang
}
```

A card carries its `kindVersion`. While an older version is still in play (6.3) the client
definition returns that version's `Play` and `Reveal`. The components use the same records as the
server, from `Quesshi.Kinds`. The editor calls the kind's own `Validate`, so the admin sees
exactly the server's verdict before saving. An unknown id renders a "not supported, reload"
placeholder.

This replaces the `@if (card.Kind == 1) ... else if (card.Kind == 2)` branches in `Play.razor`,
`Live.razor`, `Duel.razor`, `Voting.razor` and the admin question pages. The existing
`SortList`, `SortReveal`, `WorldMap`, `MapField` and `MapReveal` components become the client
halves of `order` and `map_pick`.

### 6.5 Fixtures

Each kind's test folder holds payload, resolved value, expected card and reveal, sample answers
(including duplicates, boundary values and malformed input), expected stored form and credit. The
same fixtures drive the kind's unit tests and its components' bUnit tests.

### 6.6 First kinds

| Kind | Family | Payload | Resolved at start | Answer | Graded |
|---|---|---|---|---|---|
| `choice` | trivia | exactly 4 options, correct one marked | nothing | option index | yes |
| `order` | trivia | exactly 4 items in correct order | display order | item ids as placed | yes |
| `map_pick` | trivia | country code, or point and radius; base layer | nothing | country code or point | yes |
| `poll_choice` | poll | 2–8 options | nothing | an option, or "not applicable" | no |
| `poll_roster` | poll | none | the seats that are options, with names as shown | a seat, "several of us", "none of us", or "not applicable" | no |
| `flashcard` | card | back text | nothing | self-rating 0–3 | no |

The bounds are today's (`MatchRules.ChoicesPerQuestion`, `VotingRules`); the poll answers
include today's special options. Options and items get opaque ids when authored. Today's
`SortOrder` is adapted, not moved as is: it shuffles the correct order directly, and the new
display order must be derived from the item ids and the random source so that it does not depend
on which order is correct.

Kinds checked against the contract and found to fit: multi-select with partial credit, free text
with deterministic fuzzy matching, numeric estimate scored by closeness, match pairs, image
hotspot.

### 6.7 What the contract deliberately excludes

A kind whose card depends on earlier answers, or differs per player, does not fit: items are one
row per session slot, resolved once. It is excluded now rather than half-supported.

## 7. Sessions (core)

A session is one play-through: a lobby, then a run, then a result. The engine owns everything
formats have in common; a format owns only its rules.

Today this exists three times: `Match`/`MatchGrain`/`GameEndpoints`,
`LiveMatch`/`LiveMatchGrain`/`LiveEndpoints`/`LiveHub`, and
`VotingMatch`/`VotingMatchGrain`/`VotingEndpoints`, each with its own lobby, question drawing,
answer recording, ending and notification.

### 7.1 Data

| Table | One row per | Notes |
|---|---|---|
| `sessions` | Session | Status, shared position, `due_at`, `version`, content spec, rules, working state, result |
| `session_participants` | Player in a session | Role, status, personal position, score, place, outcome, `eligible`, `ranked`, seat state and result |
| `session_items` | Slot | A snapshot of the question as drawn, plus resolved value, card and reveal |
| `session_answers` | Answer or recorded miss | Normalised answer, credit, points, time taken, receipt time |
| `session_invites` | Pending invitation | Durable, so a reconnecting player still sees it |

Decisions inside those tables:

- **Position, and whose it is.** A position is `(phase, slot, step)`; `step` counts turns inside a
  slot and is 0 for most formats. A format declares its pacing:
  - *shared* (`live`, `poll`): the position lives on the session. Everyone is on the same slot.
  - *personal* (`async_duel`, `flashcards`): the position lives on each participant.

  The engine uses that one authoritative position for three things: checking that a command is
  not stale, detecting a duplicate, and building the response view.
- **Snapshots.** `session_items` copies kind, version, payload, prompt, explanation and the
  category's presentation at draw time. Media rows are immutable and cannot be deleted while any
  question or snapshot points at them. Editing or deleting a question or category cannot change a
  running session or a finished one's history.
- **Answers are the record.** They are written as they arrive and never archived elsewhere.
  (Today a finished match's per-answer history exists only in grain state in Redis.)
- **`seq` on answers** is assigned by the engine from `step`. A client never chooses it.
- **Roles.** A participant is a `player`, a `host` (runs a session without playing) or a
  `spectator`. Only players answer and only players count towards capacity.
- **`eligible` and `ranked`.** `eligible` is fixed when a player's own play begins: for shared
  pacing, when the session starts or when they take their seat if that is later; for personal
  pacing, when they are served their first card. It is false for a guest and stays false if the
  guest upgrades afterwards. A guest who upgrades before playing anything is eligible. `ranked` is written at the end and is true
  only when the seat is eligible, is a `player` seat, the format is scored, and the session
  ended as `completed` or `forfeit` with a real outcome. Plugins read `eligible` and `ranked`,
  never the player's current state. Points earned in a run that started as a guest never count on
  leaderboards.
- **Reveal release.** `revealed_slot` (on the session for shared pacing, on the seat for personal
  pacing) records up to which slot a viewer has been shown answers. The format moves it; the
  engine enforces two things with it: a reveal is served only at or below it, and for a gradable
  kind an answer is refused from anyone who has already been shown that slot's reveal.
- **A session lives at most 90 days** from its start. The engine keeps `due_at` at or before that
  limit even when a format clears its own deadline. The limit is checked before any format or kind
  code is called, and reaching it is a core ending (`expired`, 7.5), so it works even for a session
  whose play code has since been removed.
- **Working state versus result.** `state` on the session and on each seat is working memory while
  a session runs. At the end the format hands the engine a **result** (session-level and per seat)
  holding any durable fact the finished view needs beyond places, scores and answers: elimination
  round, forfeit, abandonment, a no-contest reason. The engine stores it and empties the session's
  working state. A finished session is rendered from results and relational rows only.
- **Versions on what outlives the session.** `rules` and `result` carry a version, and the format
  keeps a small read-time upgrader for each old one, as kinds do for snapshots. Working state is
  upgraded step by step through every version an active session can still hold, which the 90-day
  limit bounds.
- **`ended_tz`** records the tenant's timezone at the moment a session ended. Leaderboard periods
  are derived from the end time and this value.
- **Codes** are reserved only while a session is in the lobby or running, enforced by a partial
  unique index. Finished sessions are addressed by id.

### 7.2 Content sources

Where a session's items come from is engine-level, not format-level:

```csharp
public sealed record ContentSpec(string Source, JsonElement Params, int Count);
```

`Source` names a **source plugin**, and `Params` is validated by that plugin:

```csharp
public interface ISource
{
    string Id { get; }                                  // "bank", "pack"
    Type Params { get; }
    // The same access check as Pick performs, run early only to give a prompt error.
    Task<bool> AuthorizeAsync(ITenantTx tx, JsonElement p, PlayerId actor);
    // Returns question ids for the engine to snapshot and resolve, or ready snapshots.
    Task<Picked> PickAsync(ITenantTx tx, JsonElement p, DrawPlan plan, PlayerId actor);
}
```

- A source is entitled and enabled per tenant like any plugin.
- **Access is checked where the items are read.** `PickAsync` runs in the transaction that starts
  the session, on behalf of the session's owner. It locks what it reads from (for a pack, the pack
  row) and checks access under that lock, so a pack made private a moment earlier cannot be drawn
  from. `AuthorizeAsync` is never trusted later.
- Items are drawn once, when the session starts. The `bank` source is today's
  `QuestionSetBuilder` and `CategorySelection`, kept with their tests as the specification:
  - eligible categories are the active ones that are also on the allow-list for the kind family
    being drawn (today's `ContentSettings`, now the `bank` source's settings, one list per family);
  - a player may choose at most three; choosing none picks up to three at random;
  - slots rotate through the chosen categories and ramp difficulty with `LevelForSlot`'s exact
    rounding; allowed levels are de-duplicated and sorted;
  - no question repeats in a session; when a bucket is empty the draw falls back first within the
    chosen categories, then across the allowed levels.

  A format supplies the plan (count, ramp, accepted kinds). Sessions made by matchmaking may only
  use `bank`.
- A source may return **ready snapshots** instead of ids. That is how a fixed set (a daily
  challenge) gives every player identical items, already resolved. The core does not trust them:
  it checks the count, that every kind is accepted by the format and enabled for the tenant, that
  the registered kind still plays that version, that media belongs to the tenant, and that
  payload, resolved value, card and reveal deserialise into the kind's records.
- If the plan cannot be filled, the start fails with `not_enough_questions`; if the source has
  been turned off since the lobby was made, with `source_unavailable`. The lobby stays open.

### 7.3 What the engine implements once

- **Lobby:** create, join by code or link, invite, leave, change settings, capacity, start,
  cancel, expiry (through `due_at`). As today, the owner leaving a lobby cancels it. Capacity
  (within the tenant's limit, today's `LobbySettings`), permitted roles and seat numbers are
  validated inside the grain. Question counts are today's choices (10, 20, 30, 40, 50, 100).
- **Creation is an insert, not a grain call.** `ISessions.CreateAsync(tx, ...)` inserts a new
  session in the caller's transaction, and from then on the grain is the only writer.
  - *The id comes from the caller.* The client (or the plugin) generates the session id, so a
    retried create does not make a second session. A replay is accepted only when the tenant, the
    creator and the request's format, content and rules all match the existing row; anything else
    is refused as `id_conflict` and returns nothing about that row.
  - *Three shapes.* A **lobby** with its owner seated. A **started** session with its whole
    roster, its items drawn and snapshotted, and the format's `Start` applied; used for solo
    formats and for a `live` matchmaking pair, so that pairing and starting are one transaction.
    An **open** session, for formats that allow joining after the start: started, items drawn,
    one seat, still joinable until it is full or reaches its forfeit deadline. That is today's
    async challenge, which its creator can play before anyone else arrives.
  - *The grain is called only after the commit.* A grain that activates and finds no row
    deactivates at once; it never remembers an absence.
- **Solo sessions:** a format whose spec says "no lobby" is created already running.
- **Drawing, resolving and snapshotting items.**
- **Answer handling:** load the item, normalise, grade, hand the graded answer to the format,
  write the single answer row.
- **Position checks and idempotency** for every command (7.4).
- **Timers:** acting when `due_at` passes (7.5).
- **Settlement:** places, outcomes, results, and emitting `SessionEnded` (7.6).
- **Views:** asking the format for each viewer's view and delivering it (section 9).
- **Rematch,** for formats that declare it (today only `live`): a new lobby with the same
  settings and the same owner, its capacity clamped to the tenant's current limit, and the other
  participants invited. It is idempotent through a unique `rematch_of`.
- **No seat exclusivity.** Today's rule is kept: sitting in more than one lobby is allowed, and
  the client only warns. An enforced "one at a time" rule is not built until a format needs it.
- **Invitations,** as today: a player may invite a friend or someone they recently played with,
  never a guest. With the `friends` plugin off, only recent co-players can be invited. An
  invitation expires by its format's rule (today: a `live` one with its lobby, a `poll` one ten
  minutes after it was sent). Expired invitations are left out of the inbox and refused on accept.
  A player can also decline one.

### 7.4 One execution model: the session grain

There is **one grain type**, `SessionGrain`, for every format. Its key is
`"{tenantId}/{sessionId}"`. The tenant is read from the key and passed explicitly to every
database call; there is no ambient tenant and no default tenant. (Today's
`TenantGrainCallFilter`, `RequestContext` tenant and the special-cased `"quesshi"` key prefix are
removed.)

**The grain is the session's only writer.** It is not reentrant: Orleans runs one command at a
time, to completion, in the order they arrive.

**What the grain holds.** On activation it loads, in one read-only repeatable-read transaction:
the session row, its seats, the items for the slots currently open (with payload and resolved
value, which grading needs) and, for each seat, whether it has answered them and at which
revision. Before it calls a format for a
command it makes sure the item for the command's slot and the one after it are in memory. The
working state is immutable: a transition produces a new state value and a list of row changes,
and never modifies the current one in place.

**Every command** runs these steps:

1. **Reject if terminal.** A finished or cancelled session is immutable; return the current view.
2. **Reconcile.** If `due_at` has passed, apply the timeout first (7.5). If that made the
   session terminal, stop and return the current view. If the session is backing off after a
   failure, change nothing and answer `session_busy`; the client retries shortly.
3. **Check position.** The command names the `(phase, slot, step)` it was issued at. The grain
   compares it with the authoritative position for this format's pacing: the session's, or this
   participant's. If they differ the command is stale: nothing changes and the current view is
   returned.
4. **Call the format**, which returns the new state and the changes.
5. **Write one transaction** as `quesshi_app` inside the tenant. Its **first** statement is

   ```sql
   update sessions set version = version + 1, ...
   where tenant_id = @tenant and id = @id and version = @expected
   returning version;
   ```

   If that returns no row, another activation has written since: the transaction is rolled back
   before anything else is written and the grain raises `StaleActivation`. Otherwise the answer
   rows, seat changes, event jobs and `pg_notify` follow in the same transaction.
6. **Then one of four things is true,** and they are handled differently:

   | Outcome | What the grain does |
   |---|---|
   | Commit confirmed | The new state becomes current. The caller's view is rendered and returned |
   | Rolled back for certain, by this command (a constraint, or the format threw before writing) | The new state is dropped. The current state is still right |
   | **Stale activation** (the fencing update matched no row) | Another activation has written, so nothing this grain holds can be trusted. It drops everything, disposes its timer, calls `DeactivateOnIdle()` and returns a retryable error |
   | Unknown (connection lost during commit) | The same as a stale activation: it cannot know, so it drops everything and deactivates. The next call activates a fresh grain from the database |
   | Committed, then something later failed (rendering, arming the timer) | The commit stands and the new state is current. The failure is logged; the client still converges from the stream. It is never treated as a failed transition |

   A client that gets an error simply retries the same command. Position and duplicate checks make
   the retry safe whichever of the outcomes actually happened.

The version condition is what makes the model safe when Orleans briefly has two activations of
the same grain (after a silo is lost or a network split). Correctness never depends on Orleans'
single-activation behaviour, only on that conditional write.

**No Orleans grain storage is used.** The grain's durable state is the relational rows, written in
step 5. No aggregate is stored as a blob through a storage provider, so there are no old aggregate
shapes to keep readable and no finished grains to wake. (The design still has versioned JSON of
its own: item snapshots, working state, rules and results, with the upgrade rules of 6.3 and 7.1.)

**Reads do not use the grain.** Views for streams, the session list, history and every admin
screen are read from PostgreSQL. The one exception is the view returned to the caller of a
command, which the grain renders because it already has the state in hand. Both paths use the
**same renderer** over the same viewing data, so the same reveal and visibility rules apply. The
client tracks each kind of state separately (session version, inbox version, tenant configuration
version, and the effects revision) and never lets an older one replace a newer one,
whichever path it came from. (Today
`GET /api/matches` wakes one grain per match in a player's history.)

**Idempotency.** Position checking makes every command safe to retry: a repeated `next` finds the
position already moved, whether it is the session's or the player's own; a repeated answer finds
its row already present and returns the current view without scoring twice.

Formats that allow an answer to be changed (`poll`) compare revisions **before** that shortcut.
The grain keeps each seat's current revision and a hash of its stored answer for the open slot. A
higher revision replaces; the same revision with the same answer is a retry; the same revision
with a different answer is `revision_conflict`; a lower revision is ignored. All four return the
current view.

**Three kinds of transition, three budgets,** measured as time the grain is busy, with 500 seats:

| Transition | What it does | Budget |
|---|---|---|
| Ordinary answer | In memory: validate, grade, score. Made durable by a group commit (7.4.1) | under 2 ms |
| Round close | Records misses and eliminations with set-based statements, moves the position | under 50 ms |
| Finish | Writes places and results for every seat with set-based statements, emits `SessionEnded` | under 100 ms |

An answer that happens to be the last one of a round also performs the close, and is measured as a
close. With batching (7.4.1) an ordinary answer's turn is the in-memory work only: its database
write is a group commit that runs while the grain accepts other commands, so the write is not grain
time. It is bounded separately by the answer commit wait (7.4.1). The only plugin code in any of them is the format and the kind, which are pure; no feature
plugin runs here.

**This is the riskiest number in the design, so it is measured first.** At 2 ms each, 500 answers
take a second of the grain's time, and a non-reentrant grain does nothing else meanwhile; more
silos do not make one session faster. The second milestone (19.2) is therefore a prototype of
exactly this path on Orleans and PostgreSQL, before any format or screen is built on it. It runs
two silos against one session and measures the whole occupied turn, including pool waits: 500
answers arriving in the last 250 ms before the deadline, a full burst against the grace period
(7.5), and the three budgets while views are being pushed to 500 streams. Its result decides one
of: the design stands; the player limit for `live` is lowered (today's default is 20, and the
platform maximum is 500); or answers are written in batches, which would then be designed before
the engine is built.

#### 7.4.1 Batched answers

The benchmark of 19.2 showed that one transaction per answer cannot meet the ordinary-answer
budget with 100 or more seats, so answers are made durable in **batches**. Nothing else in 7.4
changes: the grain is still the session's only writer, every write is inside the tenant
transaction as `quesshi_app`, the session version fences it, and no feature plugin runs in any of
these turns (only the format and the kind, which are pure).

**One gate instead of non-reentrancy.** `SessionGrain` is reentrant so a command that is waiting
for its batch to commit does not stop the grain from taking the next one. All state changes and
all writes still happen one at a time inside a single first-in, first-out gate: every entry point
(command, lobby command, timeout) takes the gate before it reads or changes the working state.
The only thing done outside the gate is waiting for a batch to commit, and rendering the view
from the committed snapshot the batch returned.

**Working and committed state.** The grain keeps the *committed* state (what the database holds,
at the version it last wrote) and the *working* state (committed state plus the answers accepted
but not yet written). A transition is always computed from the working state. The next write
stores the difference between the two.

**An answer is deferred only when it is ordinary.** After the format has applied an `answer`
command, the grain defers it if the session is not ended and the transition changed neither the
session's position, deadline, status nor result. Such an answer is validated, graded, scored and
added to the working state in memory, and its caller waits. Everything else (a close, a finish,
a timeout, a change of position or deadline, any other command type) is written at once by the
turn that made it. That write carries every deferred answer in front of it, so a close sees them
all and records misses only for seats that have not answered.

**Group commit.** The first deferred answer after a write queues a *flush turn* on the gate. Because
the gate is first in, first out, every answer that arrived before the flush turn is applied in
memory first and rides in the same batch; answers that arrive while the batch is being written
queue behind it and form the next batch. A batch is written as one transaction:

1. the fencing update of 7.4 step 5, which is still the first statement, raises the session
   version **once for the whole batch**;
2. the answer rows and the seats' scores in **set-based** statements (one statement per table,
   not one per row), then the event jobs and `pg_notify`.

**Durable acknowledgement.** A caller is told `ok` only after the transaction that holds its
answer has committed. It then receives the view rendered from the state that transaction wrote.
Until then nothing reports the answer as accepted, and a view or stream never shows an answer
that is not committed.

**Failure.** A batch is one unit. If its transaction rolls back for certain, the working state is
reset to the committed state and every caller in the batch gets the retryable `write_failed`. If
the fencing update matches no row (stale activation) or the commit outcome is unknown, the grain
does what 7.4 step 6 says: it drops everything, deactivates and every caller in the batch gets a
retryable error. A caller that retries finds its answer already stored (the commit did happen) and
gets the current view without being scored twice, or finds none and answers again. The retry is
judged by its new receipt time, so an answer that was not made durable before the format grace
ended is refused as late.

**Deadline, grace and timeout.** Eligibility is decided when the turn starts, from the receipt
time and the grace, exactly as before; being in a batch does not extend or shorten it. A deferred
answer is part of the working state, so a timeout turn that runs while it waits sees it: the close
and the batch commit in the same transaction and the answer is acknowledged as part of that
commit. An answer whose receipt time is after the deadline plus grace is refused with
`late_answer` and never enters a batch. A round closes once, because a close moves the
position and every later command for the old position is stale. The finish writes the standings
and emits `SessionEnded` once, in the transaction that ends the session.

**Budgets.** The *ordinary answer* budget is the time the grain is busy with the answer: its
in-memory turn. The batch transaction is not part of it because the grain is not occupied while a
batch is written. Instead the *answer commit wait*, from the moment an answer is accepted until its
batch has committed and it is acknowledged, is bounded to under 2 seconds in every scenario (including
a spread of answers over the whole question, where no burst drain applies); a burst must also drain
within 2 seconds of the deadline. The *round close* and *finish*
budgets are the whole turn, including the write of every deferred answer ahead of it, and the
set-based writes of misses, seats and results.

**Measured maximum.** With this path the two-silo PostgreSQL matrix of [the benchmark](benchmark.md)
selects **2** as the live player maximum: the largest tested count that met every budget above with
zero on-time refusals in every scenario. Larger tested counts exceeded the ordinary-answer or round-close
budget on the shared test host; the reasons and the run-to-run noise are in the benchmark report.

### 7.5 Time, deadlines and timers

**One clock.** Receipt time is sampled on whichever silo received the request, and deadlines are
decided on the silo hosting the grain, so the silos must agree.

- At start-up, and every 30 seconds, each silo takes five readings of the database's
  `clock_timestamp()` and keeps the one with the shortest round trip. The offset is applied to the
  silo's monotonic clock; half that round trip is the **uncertainty**.
- A silo does not serve play until it has calibrated. If the uncertainty exceeds 50 ms, or the
  last successful calibration is older than five minutes, its health check fails and it stops
  accepting scored commands.
- The clock is the existing `IClock`, so tests control time. Column defaults such as `created_at`
  are informational; no rule reads them.

**Two sampling points.**

- *Receipt time* is sampled in the HTTP endpoint when the complete, size-limited command body has
  been read, before the grain is called. It travels with the command.
- *Decision time* is sampled inside the grain when it starts handling the command. It decides
  whether a deadline has passed.

**Answer eligibility, stated once.** An answer is accepted if the slot is still open when the
grain handles it **and** its receipt time is no later than the deadline plus the format's grace.
Speed is measured from receipt time, capped at the time limit, so grace never earns points.

An on-time answer that is still queued for the grain when the round is closed is refused. What
keeps that rare is capacity: the round's timeout is issued only at deadline plus grace, and the
benchmark in 7.4 shows a full burst drains inside that grace. Under real overload the loss is
accepted and counted in a metric, not hidden.

Grace is today's `MatchRules.NetworkGrace`, 2 seconds, for both timed formats.

- `async_duel`: nobody else closes the slot, so only the receipt-time test applies: an answer is
  always judged by its own receipt time first, and only a command that arrives after deadline plus
  grace turns the open slot into a miss.
- `live`: clients stop accepting input at the deadline; the round's timeout is due at the deadline
  plus grace, as today. A round with missing answers therefore reveals 2 seconds after the buzzer.

**Timers.** `sessions.due_at` is the only durable timer for play. Orleans reminders are not used.

- **In the grain:** one grain timer, registered with `RegisterGrainTimer` as a one-shot that does
  not interleave, so it fires as an ordinary turn. It is disposed and re-armed whenever the
  deadline changes. It is armed only when the deadline is within the next two minutes; a grain
  with a far deadline is left to be collected when idle, as any grain is.
- **The sweep:** a hosted service in every silo asks `due_sessions()` once a second for a batch of
  overdue session ids and calls each one's grain. This is what wakes a session whose grain is not
  active: after a restart, or for a forfeit nobody is waiting on.
  - It keeps a set of sessions it is **still calling**. A session stays in it until its call has
    actually returned, however long that takes, because giving up waiting does not stop the call.
    The set is capped at 256.
  - It keeps a second set of sessions it is **holding back** after a call failed, each with a time
    to try again.
  - Both sets are passed to `due_sessions()` to be left out, so stuck or failing sessions cannot
    hide the others behind the batch limit. The sweep never writes to a session.
- Either path is an **internal timeout**. It is not a player command: it has no player and no
  position, and is identified only by the `due_at` it was armed for. The grain applies it only if
  the session's `due_at` still equals that value and has passed, then calls the format's
  `Timeout`.

A timeout may therefore be *delivered* more than once; it is *applied* once.

**Which time a transition uses** is the format's choice; the engine hands it the decision time
and the deadline that was missed. `live` keeps today's anchors (`LiveMatch.StepOnce`): phases are
measured from their scheduled boundaries, so a round closed by the buzzer shows its reveal until
three seconds after the question's scheduled end.

**When applying a timeout fails,** the cause decides what happens:

- **The format or a kind threw** (a bug in pure code, before anything was written). The grain
  itself records it, as an ordinary version-guarded write: `fault_count` goes up and `retry_at`
  is pushed out with backoff. After five such failures the core ends the session.
- **Anything else** (database unavailable, a timeout, a stale activation, an unknown commit) is
  not the session's fault. Nothing is written. The sweep holds the session back in memory with a
  short capped backoff and tries again.

While `retry_at` is in the future the session is skipped by `due_sessions()` and by the reconcile
step of ordinary commands. The grain timer keeps two separate things: *when to wake*, which is
the later of `due_at` and `retry_at`, and *which deadline it is for*, which is always the original
`due_at`. When it fires, the grain checks both that the deadline still matches and that the
back-off has passed. A successful transition clears both fields. Only the grain ever writes them.

**Core endings need no format.** Five endings are performed by the core alone, without calling
the format or reading its state, so they work even when the format is what is broken: a lobby
cancelled by its owner (`owner`), a session ended by an admin (`admin`, today's "end this live
duel" on the admin screen), a lobby or session past its limit (`expired`), a `fault`, and an
`outage`. The session becomes `cancelled` with that reason; every seat gets outcome `none` and
`ranked` false; deadlines and retry state are cleared; the client shows a generic
"this session was cancelled" screen that needs no format code. No penalties follow from a core
ending.

**After an outage.** If a `live` phase's deadline is applied more than 40 seconds late (today's
`LiveRules.StaleAfter`, twice the question time), the engine does not replay the missed rounds. It
ends the session with the core ending `outage`. Deadlines of `async_duel`, `poll`, `flashcards`
and lobbies are simply applied late.

How quickly a live session recovers depends on what failed, and each case is measured rather than
assumed: a restarted process is picked up by the next player command or the next sweep; a silo
that dies is unreachable until Orleans' membership declares it dead, which is a configured number
of seconds; a network split can take longer. Any that exceed the limit end as `outage`.

**Lazy deadlines.** Deadlines that concern one present player are not timers. In `async_duel`
nobody fires when a question's 20 seconds pass; that player's next command sees the expired slot
and records the miss. `due_at` is for things that must happen with nobody present.

### 7.6 Settlement

A session ends once: step 1 of 7.4 rejects everything afterwards. There are two ways to end.

- **The format ends it** with `s.End(reason, standings, result)`: status `finished`. The reason
  is one of three and is stored:

  | Reason | Meaning | Stats | Board points | Abandonment penalties |
  |---|---|---|---|---|
  | `completed` | Played to the end | counted | counted | apply |
  | `forfeit` | Ended by the forfeit deadline | counted | counted | apply |
  | `no_contest` | Nobody left, or nobody answered | not counted | not counted | apply |

- **The core ends it** (7.5): status `cancelled`. Nothing is counted and no penalty applies.

In the ending transaction the engine:

1. Writes each seat's place, outcome, `ranked` and seat result, marks active seats `done`,
   stores the session result, sets the status, `ended_at` and `ended_tz`, empties
   `state`, `due_at` and the retry fields. For a format ending it checks that the standings cover
   every player seat.
2. Emits `SessionEnded` (5.5).

That is all. No feature plugin runs here, so nothing a feature does can stop a session from
ending. Stats and boards follow through their event handlers, exactly
once.

## 8. Formats

### 8.1 Contract

```csharp
public interface IFormat
{
    string Id { get; }
    FormatSpec Spec { get; }
    FormatVersions Versions { get; }            // State, Rules, Result

    JsonElement ReadRules(JsonElement raw);     // validate and apply defaults
    DrawPlan Plan(ContentSpec content, JsonElement rules);

    void Start(ISession s);                     // lobby → first phase
    void Apply(ISession s, Command command);    // a player's answer, next, leave or action
    void Timeout(ISession s);                   // the session's deadline passed; no player involved
    object View(IViewing v);                    // what this viewer may see right now

    JsonElement UpgradeState(int from, JsonElement state);    // every version an active session can hold
    JsonElement UpgradeRules(int from, JsonElement rules);    // kept for every old version
    JsonElement UpgradeResult(int from, JsonElement result);  // kept for every old version
}

public sealed record FormatSpec(
    Pacing Pacing,                 // Shared or Personal: whose position commands are checked against
    int MinPlayers, int MaxPlayers,
    bool Lobby,                    // false: created already running (solo formats)
    bool Scored,                   // results may count for stats and boards
    Func<IKind, bool> Accepts,
    bool Guests,                   // may guests join
    MatchRule? Matchmaking,        // null: no random-opponent queue. Else: which settings two
                                   // players must share, and whether both must be online
    bool Rematch,                  // offers a rematch when it ends
    IReadOnlyList<Role> Roles,
    TimeSpan Grace,                // see 7.5
    IReadOnlyDictionary<string, Type> Actions);   // format-defined verbs and their payload records

public sealed record Command(
    CommandType Type,              // Answer, Next, Leave, Action
    PlayerId Player,
    Position At,                   // the (phase, slot, step) the client was looking at
    int Revision,                  // 0 unless the format allows changing an answer. Sent by the
                                   // client, carried through HTTP and the grain, stored with the answer
    string? Name,                  // for Action
    JsonElement? Payload,          // validated by the kind (answer) or the action's record
    DateTimeOffset Received);      // receipt time, set by the endpoint
```

The grain's own interface takes a serialisable form of this (`SessionCommand`, with generated
Orleans serialisers and the payload as JSON text) and returns the caller's view the same way.

Format methods are synchronous on purpose. A format reads and changes the in-memory session
through `ISession`; the engine does all the input and output around it.

**What a format can and cannot touch.** `ISession` gives a format the session, its own state,
the seats, and item *metadata* (kind, level, category). It never gives a format a question's
payload. The operations are:

```csharp
// Answer handling is two calls, so a format can decide between them.
Graded g = s.Grade(command);        // kind.Normalize then kind.Grade; changes nothing
                                    // g: Stored, IsGraded, Credit, Taken, OnTime, Duplicate
s.Record(g, points);                // the one authoritative answer
s.Replace(g, revision);             // formats that let an answer change until the slot closes:
                                    // updates the same row if the revision is higher; does not
                                    // count as another answer

s.RecordMiss(player, slot);         // personal pacing: one player's expired slot
s.RecordMisses(slot);               // shared pacing: every expected seat without an answer
s.SetPosition(phase, slot, step);   // shared pacing
s.SetSeatPosition(player, phase, slot, step);   // personal pacing; does not touch the clock
s.Serve(player, slot);              // personal pacing: phase "question" at this slot, clock starts now
s.Release(slot);                    // shared pacing: answers up to this slot may be shown
s.ReleaseTo(player, slot);          // personal pacing: the same, for one player
s.SetDeadline(at);                  // or s.ClearDeadline(); never beyond the 90-day limit
s.Eliminate(player, why);
s.End(reason, standings, result);   // reason: Completed, Forfeit or NoContest; the engine settles
```

Between `Grade` and `Record` a format may reject the answer, compute points, or decide an
elimination from the grade. `g.Stored` is the normalised answer, so a format can tell that a pick
was already taken.

**Formats cannot leak what they never hold.** In `View`, the handle exposes `Card(slot)` and
`Reveal(slot)`, read from the snapshot. `Reveal` and other players' answers are served only for
slots the format has released to that viewer, and the engine refuses a graded answer from a
viewer who has been shown the reveal (7.1). So today's rule that an async player sees nothing of
the opponent's run until their own is finished is one line of format logic that the engine
enforces.

Formats are pure (section 4): given the same session data, command and clock they make the same
changes. They are unit-tested against an in-memory `ISession`, with no grain and no database.

**Client definition.**

```csharp
public sealed class LiveFormatClient : IFormatClient
{
    public string Id => "live";
    public Type Settings => typeof(LiveSettings);   // lobby settings form for this format's rules
    public Type Screen   => typeof(LiveScreen);     // the running session, given the view
    public Type Result   => typeof(LiveResult);     // the finished session
}
```

One shared `Session.razor` page hosts the lobby (written once) and then the format's `Screen` or
`Result`. It replaces `Play.razor`, `Duel.razor`, `Live.razor`, `Voting.razor`, `Lobby.razor` and
`Join.razor`'s per-mode branches.

### 8.2 First formats

| Format | Today | Pacing | Scoring | Ends when |
|---|---|---|---|---|
| `async_duel` | `Match` | Personal; per-question clock, evaluated lazily | Points and speed bonus | Everyone done, or forfeit 48 h after creation |
| `live` | `LiveMatch` | Shared: countdown, question, reveal | Points and speed bonus | Last round, one player left, or none left |
| `poll` | `VotingMatch` | Shared; advances when every active player has answered | None; results show agreement | Last question, or fewer than two active players |
| `flashcards` | new | Personal, solo, untimed, no lobby | Self-rated | Deck exhausted, or 30 days idle |

`flashcards` is in the first set on purpose: it is solo, untimed and ungraded, so it proves the
contract is not shaped around duels.

**`async_duel`, exactly.** Each player moves through their own positions:

| From | Command | To | Effect |
|---|---|---|---|
| `idle` | `next` | `question` at slot 0 | Card served, clock starts |
| `question` | `answer` received by deadline plus grace | `answered`, same slot | Answer recorded and scored; that slot's reveal released to this player |
| `question` | any command received after deadline plus grace | `answered`, same slot | Miss recorded; reveal released. The late command itself is then stale and returns the view |
| `answered` | `next` | `question` at the next slot, or `done` after the last | Card served, clock starts |

- `next` while a card is open and in time is stale and changes nothing. The clock starts only when
  a card is served.
- A player in `done` sees the other players' runs; until then they see nothing of them.
- **Forfeit at 48 hours from the session's creation,** as today (`MatchRules.ForfeitAfter` from
  `CreatedAt`): the one deadline covers the lobby and the run. Players who finished keep their
  runs. Unfinished players' seats become `left`, with `forfeited` and the number answered in the
  seat result; their unplayed slots are *not* turned into miss rows. Standings are built by score
  even if nobody answered, as today, and the ending is `forfeit`.

**`live`, exactly.** The rules are today's `LiveMatch` and `LiveRules`, moved, with their tests.

- **Phases:** one `countdown` (3 s) at the start, then for each slot `question` (20 s) and
  `reveal` (3 s).
- **Who is expected to answer** in a round is fixed when the round opens: seats with role `player`
  and status `active`. State holds `expected` and `answered` for that set. A player who leaves or
  is eliminated before answering lowers `expected`; one who had already answered stays counted in
  both.
- **A round closes** when `answered` reaches `expected`, or at `due_at` (the deadline plus grace).
- **Misses:** three *consecutive* rounds without an answer eliminate a player
  (`LiveRules.MissesBeforeAbandon`). Any answer resets the count.
- **Abandoners** are players who left a running session or were eliminated for missing. The
  format marks them in the seat result with the round they went out in.
- **Standings,** as today's `BuildStandings`: everyone who finished ranks above every abandoner,
  whatever their scores. Finishers rank by score. Abandoners rank by how late they went out, and
  their standing score is zero. Ties share a place and the next place is skipped. The sole holder
  of first place wins; several sharing it draw; everyone else loses.
- **One player left:** the session finishes as `completed`.
- **Nobody left:** `no_contest`.

**`poll`, exactly.** The rules are today's `VotingMatch`, `VotingRules` and `VotingResults`,
moved, with their tests.

- **Answering:** a player may change their answer until the slot closes. The first answer is
  recorded and counted. A later command with a **higher** revision replaces it in place and does
  not change the count. The **same** revision with the same answer is a retry and changes nothing;
  the same revision with a different answer is refused as `revision_conflict`. The player's view
  carries their current revision, so a second tab or a reconnect continues from it.
- "Not applicable" is accepted for both poll kinds, as today.
- **A slot closes** when every active player has an answer.
- **Idle expiry is per slot:** 48 hours after a slot was served, players who have not answered it
  become inactive (`left`) and the slot is re-evaluated, so the remaining players continue. With
  fewer than two active players the session ends as `no_contest`.
- **Visibility:** other players' answers and each slot's result are released only when the session
  ends, as today.
- **Results** are computed by today's `VotingResults` unchanged: "not applicable" answers are left
  out of pair comparisons, and a slot needs at least two comparable answers to have an agreement
  figure.
- Up to 8 participants (`VotingRules.MaxParticipants`).

**`flashcards`, exactly.** One player, their own positions:

| From | Command | To | Effect |
|---|---|---|---|
| `front` | action `flip` | `back`, same slot | The back (the reveal) is released to the player. No clock |
| `back` | `answer` with a rating | `front` at the next slot, or `done` | Rating recorded |

Showing the back before the rating is allowed because the kind is not gradable. The session stays
resumable. `Start` sets the deadline to 30 days ahead, and every flip and every rating sets it
again (never past the 90-day limit). When it passes, the format finishes the session with what
was rated, which may be nothing.

### 8.3 Scoring

Scoring belongs to the format. The shared helper keeps today's `Scoring.Score` with credit in
place of the boolean:

```
points = round((100 + 60 × time_remaining_fraction) × level_weight × credit)
```

`time_remaining_fraction` is clamped to [0, 1]. Level weights are today's (0.5, 0.8, 1.0, 1.3,
1.6); a question with no level uses 1.0.

### 8.4 What later ideas would need

| Idea | What it is | What it needs |
|---|---|---|
| Elimination live (wrong answer removes you) | Format | Nothing new. The format decides from the grade between `Grade` and `Record` |
| Turn-based duel over a shared pool | Format | Nothing new. `step` is the turn. The format decides whether a rejected pick costs the turn; if it does, it records the attempt with zero credit and advances `step` |
| Host-led live quiz with a shared screen | Format | Its own table of screen links (hashed, expiring, redeemable into a `spectator` seat that survives reconnects). Host-only actions are checked by role |
| Team play | Format | A team-aware format: turn order, combined points and standings are session rules. Team assignment is frozen into seat state and result |
| Daily challenge | Feature + source | A table holding each day's complete item snapshots and a unique attempt per player; a source that returns those snapshots; its own board |
| Knockout tournament | Feature | Tournament, rounds and slots with unique constraints; it advances on `SessionEnded` and handles every ending reason |
| Achievements | Feature | A table of grants, unique per player and achievement, written from event handlers |
| Web push | Feature | A table of browser subscriptions; jobs that send and prune them; it reads the inbox |
| Spaced repetition | Feature + source | A per-player card schedule. **Needs a core addition:** a reaction to each recorded answer inside the command (5.3) |

## 9. Delivery (core)

### 9.1 The server pushes views, not events

A signed-in client holds one stream for the whole app, `GET /api/stream`, optionally with
`?session={id}` for the session it is looking at. The shell owns it, not a page: it stays open on
the home and profile screens too, because it also carries the inbox and is what makes the player
count as online. Changing the watched session reconnects it. Whenever the watched session changes,
the server sends that viewer their complete current view. There is no event log and no replay: a
reconnecting client receives the current view.

This replaces `LiveHub`, `LobbyHub`, the three notifier ports (`ILiveNotifier` with its ten
methods, `IVotingNotifier`, `ILobbyNotifier`) and the client's `LiveClient` with its fourteen
events and `LobbyClient`.

```csharp
public sealed record SessionView(
    string Id, string Code, string Format, SessionStatus Status,
    string Phase, int Slot, int Step, long Version,
    long ConfigVersion,            // the tenant's; a change tells the client to re-read its config
    DateTimeOffset Now,            // server time when rendered
    DateTimeOffset? Deadline,      // when the current phase ends for input
    MySeat Me,
    RosterView Roster,
    ContentSpec Content, JsonElement Rules,
    JsonElement Body);             // defined by the format

// Up to 50 seats: everyone. Above that: counts, the top ten, and the viewer's neighbours.
public abstract record RosterView;

public sealed record Card(int Slot, int Total, string Kind, int KindVersion, string Prompt,
    MediaDto? Media, int? Level, CategoryRef? Category, int? LimitMs, JsonElement Body);
public sealed record Reveal(string Kind, JsonElement Body, string? Explanation);
```

The full roster of a large session is fetched on demand from `GET /api/sessions/{id}/roster`. The
client draws its countdown from `Now` and `Deadline`, not from its own clock, and derives effects
(sounds, transitions) by comparing the previous view with the new one.

**In the Blazor client** one small `StreamClient` service owns the stream:

- an `HttpClient` request sent with `HttpCompletionOption.ResponseHeadersRead` and browser
  response streaming on, read with the framework's `SseParser`; the browser's `EventSource` is not
  used, so the request carries normal headers and the sign-in cookie;
- no overall timeout on that request; a cancellation token ends it at sign-out, and the old
  reader is cancelled before a new one starts or the watched session changes;
- it checks the status and content type before reading; `401`, `403` and `client_outdated` are
  final and are never retried; anything else reconnects with backoff.

### 9.2 What delivery promises, and what it does not

- **A client always converges on the current view.** That is the guarantee.
- **A client may skip a phase.** A slow connection or a reconnect can go from one question
  straight to the next without ever showing the reveal between them. The format's view includes
  the previous slot's outcome so nothing is lost for the player, only an animation.
- **One snapshot per render.** A view is built from a single read-only repeatable-read
  transaction, so it never mixes version N of the session with version N+1 of its answers.
- **Versions only move forward.** The server keeps the last version sent per subscriber and drops
  anything older. The client does the same for both stream messages and command responses.
- **Phase changes are sent at once;** updates within a phase (scores, answered counts) are
  coalesced to a few per second.

### 9.3 Fan-out

- `pg_notify` is issued in the grain's transaction and carries the tenant id, session id and
  version. Each silo holds one dedicated listening connection.
- On a notification a silo renders views **from the database**, not from the grain, for its own
  subscribers. The shared part is rendered and serialised once; the personal part is small.
  Rendering for one session never overlaps itself.
- **Bounded memory per stream.** A subscriber holds at most one pending view, in a bounded channel
  of size one that drops the older item. Each write and flush has its own 10-second deadline,
  enforced by a separate timer that aborts the connection, because a blocked write cannot check
  its own clock. Response compression is off for the stream route, and a frame is capped at
  64 KB. Two rules keep every view under that: a question's card and reveal together may not
  exceed 16 KB when serialised, checked when it is approved and again when it is drawn; and a
  finished session's view carries standings in the same windowed form as a large roster (the top
  ten and the viewer's neighbours), with the full standings and the per-question history fetched
  in pages. The size of every complete frame is checked before it is sent. When a client disconnects, its subscription is removed. This bounds the server's side;
  it cannot tell whether a browser behind a proxy is really consuming, which is checked through
  the real proxy.
- **The listening connection** is a dedicated, non-pooled-for-queries connection in a loop that
  waits for notifications. After a reconnect it issues `listen` again and only then runs the
  immediate reconcile.
- **Notifications are an optimisation, not a guarantee.** Every 2 seconds each silo re-reads, for
  its own subscribers only: session versions, inbox versions, each tenant's `config_version`,
  whether each player is still allowed in (not banned, not deleted), whether each stream's
  sign-in session still exists and has not passed its expiry, and, for finished sessions someone
  is still looking at, which plugins' effects are in. Tenant data is read tenant by tenant as
  `quesshi_app`; sign-in sessions in one query on the global table as `quesshi_system`. It
  re-sends what moved and closes what is no longer allowed. When its listening connection drops
  and reconnects it does this at once. A lost notification delays a view by at most 2 seconds;
  question timing is unaffected, because scoring uses server receipt times.
- **Configuration changes are pushed on their own.** Changing a capability or branding updates
  the row and `config_version` in one transaction and notifies. Every stream of that tenant gets a
  small `config` message, whether or not any session moved.

### 9.4 Commands

`POST /api/sessions/{id}/commands` with
`{type, at: {phase, slot, step}, revision?, name?, payload?}`. The
endpoint stamps the receipt time and calls the grain; the response is the caller's new view.
Errors are `{error: "<stable_code>"}` with a fitting status.

### 9.5 The inbox

Things not tied to one open session (invitations, rematch offers, a matchmaking result) are
**views of durable state**, delivered the same way as session views:

- Invitations are rows in `session_invites`; a matchmaking result is simply a session the player
  is seated in. A feature plugin adds its own items through its inbox function.
- Anything that changes a player's inbox calls `IInbox.Changed(tx, playerId)`. The bump is
  written to `player_inbox` as the last thing in the transaction (5.4) and notifies. The stream
  sends the inbox on connect and whenever the version moves, and the 2-second reconcile covers a
  lost notification.

### 9.6 Presence

An open stream refreshes `players.last_seen_at` every 30 seconds. "Online" means seen in the last
60 seconds. This is approximate by design and works across tabs and silos without a shared
connection registry. Lobby views are re-rendered every 30 seconds so the online marks stay fresh.
(Replaces `RedisPresence` and `RedisLiveDirectory`.)

### 9.7 Streams and revoked access

A stream closes when its sign-in session is revoked or expires, or its player is banned or
deleted. Signing out, banning and deleting send a notification that closes the streams at once;
the 2-second reconcile (9.3) catches a lost one.

### 9.8 Client and server versions

- The client sends its build id as a header on every request, including the stream.
- When the server no longer accepts that build it answers `client_outdated`, and the client
  reloads, at most once per build id, so a mismatch can never become a reload loop.
- Blazor's published assets are fingerprinted; the server keeps the previous build's files beside
  the current ones, so a tab that is mid-session can still fetch an assembly it has not loaded yet.

## 10. Tenancy (core)

### 10.1 Resolution

Every request is resolved to a tenant from its host through `tenant_hosts`, cached in memory for a
few seconds. Hosts are stored and compared in one canonical form (lower case, no port, no trailing
dot, punycode). Behind a proxy the forwarded host is honoured only when the peer is listed in
`TRUSTED_PROXIES`. An unknown host gets a 404. There is no default tenant.

Today tenants are a list in `appsettings.json` (`TenantOptions`), each with its own Mongo
database, and a missing tenant silently means `"quesshi"`. Both go.

### 10.2 Isolation

All tenants share one schema. Three layers keep them apart:

1. **Row-level security.** Every tenant-owned table, core or plugin, has `tenant_id` and a policy
   comparing it with the transaction setting `app.tenant_id`. The setting is read without a
   fallback, so a query outside a tenant transaction raises an error.
2. **Composite keys.** `tenant_id` is the first column of every primary key, and every foreign key
   between tenant-owned tables includes it.
3. **One door.** Tenant data is reached only through `ITenantDb.InTenantAsync(tenantId, fn)`,
   which opens a connection from the `quesshi_app` data source, begins a transaction and binds the
   tenant with `select set_config('app.tenant_id', @id, true)`. The resulting `ITenantTx` is
   passed to every core and plugin function involved; none opens its own.

**Three database roles, none with `BYPASSRLS`:**

| Role | Used by | Can touch |
|---|---|---|
| `quesshi_owner` | `quesshi migrate` only | Everything; owns the objects. The running server has no credential for it |
| `quesshi_app` | Endpoints, grains, plugins and all tenant work | Tenant-owned tables inside the current tenant; its own tenant's configuration (limited columns); member accounts read-only; adding a job for its tenant through one function |
| `quesshi_system` | Sign-in, tenant lookup, job runner, timer sweep, Orleans membership | Global tables and the `orleans` schema. It reaches tenant-owned data through exactly two functions |

The two functions `quesshi_system` may call:

- `due_sessions()` returns the ids of sessions whose deadline has passed.
- `account_players(account)` returns one account's live profiles across tenants. It is called only
  for the account that has just authenticated, to pre-fill a first visit, and for an account being
  deleted, to find what to scrub.

Background work that crosses tenants is always two steps: `quesshi_system` learns *which* rows are
due, then the work on each row runs as `quesshi_app` inside that row's tenant. No runtime
credential can read two tenants' data in one query.

Row security stops one tenant reading another. **Inside a tenant, who may see what is application
code:** pack visibility, question ownership and admin roles are checked by the code that owns the
data, on every read, edit, draw and report.

### 10.3 Capabilities

`tenant_capabilities(tenant_id, type, key, entitled, enabled, settings)`. `type` is `kind`,
`format`, `feature`, `signin`, `source` or `switch`; `key` is the plugin id, or for `switch` the
name of a core switch. Core switches are registered with a settings record like plugins are.

- **Rows are created by the platform** (`quesshi sync`, tenant creation). Tenant code can change
  only `enabled` and `settings`, never create a row or touch `entitled`.
- **`entitled`** is what the tenant may use; **`enabled`** is what its admins chose. Something is
  on when both are true.
- **`settings`** is validated against the plugin's settings record.
- Any change bumps `tenants.config_version` in the same transaction (9.3).

This replaces `EnabledModes` (`async`, `live`, `voting`), `TenantModeFilter`, the untyped `Rules`
dictionary, `LiveSettingsGrain` (the `live` format's enabled switch), `LobbySettingsGrain` (the
tenant's capacity limit, now a core setting) and `ContentSettings` (the `bank` source's per-family
category allow-lists, 7.2).

**Core switches after being turned off:** with `guests` off, no new guest can be created; existing
guest sign-ins keep working until they expire. With `custom_questions` off, players cannot create
or edit questions and packs; existing packs stay playable by their owners.

### 10.4 Branding and languages

`tenants.branding` holds theme tokens, logo and icon media ids, and the app name and landing texts
per language. The server serves them as `/theme.css` and `/manifest.webmanifest`, per tenant (16).

A tenant's languages are rows in `tenant_languages`: code, display name, English name and text
direction. The generation prompts use the English name. Category names fall back from the requested language to
the tenant's default. Today's `QuestionPromptBuilder` quietly treats an unknown language as
English and reads `NameEn`; both are replaced.

Languages become plain codes. The `Language` enum (`Fa`, `En`, `Nl`) and the
`NameFa`/`NameEn`/`NameNl` columns on categories are replaced by a language code string and a JSON
map of names, so a tenant can use a language the code has never heard of.

## 11. Identity and security (core)

### 11.1 Accounts and players

A person has **one account across every tenant** and **one player profile per tenant** they have
used.

- **`accounts`** is global: an id and an optional email. Sign-in identities and emailed codes are
  global too. A live account may be subject-only when an external provider did not verify an
  asserted email; stored addresses remain unique, and a deleted account must still have no email.
- **`players`** is tenant-owned: display name, avatar, language, ban state, and a nullable
  `account_id`. Sessions, stats, friendships, packs and reports reference the player, never the
  account. Tenant isolation is therefore the same as if accounts were separate.
- **First visit to a tenant:** the person is shown a "confirm your profile" step, pre-filled from
  their most recently used live profile, and nothing is published until they confirm.
- **Creating a player for an account, or attaching an account to a guest,** always starts with
  `claim_account()`. It fails if the account has been deleted, and otherwise holds the account
  against deletion until the transaction ends.
- **Guests** are players with no account.
  - *Upgrading with a new email, or one that has no player in this tenant:* the account is attached
    to the guest's player row. The id does not change, so nothing is re-keyed. `PlayerUpgraded` is
    emitted. Results earned as a guest stay unranked.
  - *Upgrading with an email that already has a player in this tenant:* refused with today's
    `AddressTaken` answer. The guest stays a guest and can sign in to the other profile instead.
  - On a successful upgrade every guest sign-in session is revoked and a new account session is
    issued.
- **Sign-in is per host.** A person signs in once on each tenant's host, with the same email, and
  lands in the same account.
- **What a tenant sees.** A tenant's admins see the email only of accounts that have a player in
  their tenant. Nothing reveals which other tenants an account uses.
- **Bans and stats are per tenant.**

**Deleting an account** is a durable workflow, not one cross-tenant transaction:

1. One transaction on the global tables: lock the account row; clear the email and set
   `deleted_at`; delete every sign-in session, external identity and pending code; list the
   account's players with `account_players()`; add one core clean-up job per tenant. Two things
   hold the account against this lock and re-check `deleted_at`: profile creation
   (`claim_account()`) and the issuing of a sign-in session. So either of them commits first and
   is covered by this deletion, or starts afterwards and is refused.
2. Per tenant, the core job locks the player, marks it deleted and scrubs its name and avatar.
   **Only when that commits** does it add one clean-up job per installed plugin. The deleted mark
   therefore always exists before any plugin clean-up runs, and event handlers skip deleted
   players (5.5), so a late event cannot bring removed data back.
3. Each plugin job runs that plugin's player clean-up under the player lock. One plugin failing
   does not hold up the others.

Clean-up jobs have no attempt limit: they are retried with capped backoff until they succeed, and
raise an alert while they are failing. They are idempotent.

Player rows stay so other people's histories remain intact. The same email later creates a new
account. Purging an abandoned guest runs the same per-tenant steps.

Admins are a separate identity, also global: one admin login with a role and active state per
tenant (`admin_memberships`), and a platform flag for whoever creates tenants and sets
entitlements. An owner may deactivate an administrator's membership only in their tenant; it
does not change the global login, any other tenant membership, or platform authority.

Today a player is one Mongo document per tenant holding email, stats, per-category accuracy,
friends, abandonments and the settled-match dedup list. That document is split: identity into
`accounts` and `players`, everything else into the plugins that own it.

### 11.2 Signing in

- **Emailed codes** keep today's mail and wording; the checking is rewritten, because today's
  service reads, compares and deletes in separate steps. They are six digits, valid ten minutes, stored as an HMAC keyed by the server
  secret. Verification is one statement that deletes the row if the code matches and has attempts
  left, so two concurrent requests cannot both succeed. Five wrong attempts void the code.
- **Other sign-in methods are plugins.** A method proves an external identity and hands the core
  `(provider, subject, email, emailVerified)`. The core matches on the subject first and links to
  an existing account by email only when the email is verified. An unverified email neither selects
  nor claims an address: the core creates a separate subject-only account, even if that address is
  stored by another account. This owner-approved core behavior differs from today's Google flow,
  which rejects unverified emails. Completion then resolves the account's player for the current
  tenant: a live player gets a signed-in outcome, while a missing player gets a profile-needed
  outcome with the usual confirmation token and no player is created before confirmation. Its
  routes are filled into a core-built group under `/api/auth/x/{method}`. Google is the first.
- **Admins** keep today's username and password sign-in with ASP.NET Identity's hasher, the
  lock-out rule, single-use reset tokens, global activation and deactivation, a unique email for
  resets, and last sign-in time. An owner may separately activate or deactivate a membership in
  their own tenant; it revokes only that tenant's sessions and does not affect global activation,
  other tenants, or a platform administrator's platform authority. The last active platform admin
  cannot be globally deactivated or removed, nor the last active owner membership of a tenant.
  Those checks are made under a lock shared by everyone changing the same thing (one advisory lock
  per tenant for owners, one global one for platform admins), so two requests cannot each remove
  "the other" owner. Deactivating an admin or removing a membership revokes the affected sessions
  in the same transaction.
- **Sessions are server-side rows, carried in a cookie.** Sign-in sets a `__Host-` cookie,
  `HttpOnly`, `Secure`, `SameSite=Lax`, holding an opaque token whose hash is a row in
  `auth_sessions`. A session row is bound to one tenant and to exactly one of an account, a guest
  player or an admin; players and admins use different cookies. Resolved sessions are cached in
  memory for 60 seconds, which is the longest a revoked session can keep working for ordinary
  requests. This replaces the two JWT bearer schemes and the token kept in the browser's storage:
  a session can now be revoked, which account deletion and bans rely on.
- **Issuing a session** for an account is one transaction on the global tables that locks the
  account against deletion and re-checks that it is not deleted.
- **Sign-in runs as `quesshi_system`** against the global tables, then creates or loads the player
  as `quesshi_app` inside the tenant. The steps are separately idempotent.

### 11.3 Other protections

- **Cross-site requests.** Every request that changes something, on a cookie session, must carry
  an antiforgery token header and an `Origin` matching the tenant's host. This holds for JSON
  routes and for the multipart upload and import routes the admin panel keeps. No route is exempt;
  the check is one filter the core applies to every route group, including plugins'.
  - The client gets its token from `GET /api/antiforgery`, which is never cached, and asks again
    after every change of identity: sign-in, sign-out, a guest upgrading. A request refused for a
    stale token fetches a new one and is retried once.
  - Players and admins have separate cookies and separate tokens, and each route group names the
    one it accepts, so an admin's cookie never authorises a player route or the reverse.
  - The keys that protect these tokens are stored in the database (`data_protection_keys`), so
    every silo accepts every other's tokens and a restart does not invalidate open tabs.
- **Guests are denied by default,** as today. A guest may join by code, play, see their own
  sessions, edit their own profile and upgrade. A guest may not create a session or lobby, invite,
  ask for a rematch, search for or add friends, report a question, or write questions and packs.
  The route builders refuse guests unless a route is marked as allowing them, in the core and in
  every plugin.
- **Rate limits.** Code requests and admin sign-in are counted in the database, per email and per
  address; general request limits use ASP.NET Core's rate limiter, per silo.
- **Audit.** Admin actions are appended to `audit_log`, which the application role can insert into
  and read but not change.

## 12. Content (core)

- **Categories** store names as a JSON map of language to text.
- **Packs** are ordered sets of questions: a player's custom quiz, a flashcard deck, a curated
  round. A question can be in several packs. Player-authored questions are reachable only through
  packs and never enter bank draws.
- **Who may use a pack:** its owner always; anyone holding the link when it is `unlisted`; any
  player of the tenant when it is `tenant`. Only the owner (or a tenant admin, for tenant-owned
  packs) may edit.
- **Duplicates:** a partial unique index on `(tenant_id, lang, family, topic)` for bank questions,
  as today's index on `(Lang, Family, Topic)`, plus the existing `PromptFingerprint` comparison at
  import and generation.
- **Search:** a trigram index on `prompt` serves the admin's text search in every language.
- **Media** is keyed by content hash and served with immutable caching. Files stay on local disk.
  Running on more than one machine requires a shared volume or object storage. Today a question
  may point at any external or relative URL; in the platform every medium is a stored asset.
  Forms and imports that are given a URL fetch it into an asset and keep its attribution; an
  import's dry run reports any URL that cannot be fetched.
- **Fetching a URL someone typed is a new power for the server, so it is fenced.** Only `http`
  and `https`; the host is resolved first and refused if it is a loopback, private, link-local or
  cloud-metadata address; the connection is made to the address that was checked; at most three
  redirects, each checked the same way; the body is read as a stream with a size limit and its
  type is checked. A relative path in an imported file refers to a file in that import's own
  media bundle, never to a URL on the server.
- **Import** keeps today's CSV and JSON pipeline; the per-kind column mapping moves into each
  kind's `IImports`.
- **Between tenants,** content is copied by export and import. Nothing is shared by reference.

## 13. The first feature plugins

| Plugin | Owns | Reacts to | How it works |
|---|---|---|---|
| `stats` | `player_results`, `player_stats`, `player_category_stats`, `question_stats` | `SessionEnded` | See below |
| `leaderboard` | `leaderboard_entries`, `leaderboard_scores` | `SessionEnded` | See below |
| `friends` | `friendships` | nothing | Friendship is mutual, as today: adding or removing writes or deletes both directions in one transaction under both players' locks. Provides `friends.ids` |
| `matchmaking` | `matchmaking_entries` | nothing | See below |
| `reports` | `question_reports` | nothing | As today, a player may report only a question from their own recent sessions, with one of the known reasons. In one transaction: lock the question row, check that, insert the report ("do nothing on conflict"), count reports in a following statement, and at three (`Question.ReportsBeforeSuppressed`) ask the core to return the question to review. An admin dismissing reports deletes them, so a corrected question starts from zero |
| `ai_generation` | `generation_runs`, `ai_budgets`, `ai_calls` | nothing | See below |

Where they come from: `stats` and `leaderboard` from `PlayerGrain`, `PlayerStats`, `Player`'s
per-category and abandonment fields, and `RedisLeaderboard`; `friends` from `Player.Friends`;
`matchmaking` from `MatchmakingGrain` and `LiveMatchmakingGrain`; `reports` from
`Question.Report`; `ai_generation` from `TopUpQuestionBank`, `GenerateVotingQuestions`,
`QuestionGeneratorGrain`, the generation logs and `IAiSpendLog`.

**`stats`.** As today, only scored formats produce player statistics; `poll` produces none. Session
creation snapshots `FormatSpec.Scored` in `sessions.scored`; the projection uses that immutable
fact rather than a seat's leaderboard eligibility, so a guest-only scored session still records
its started runs and later format configuration cannot reclassify a completed session.

- For a `finished` scored session that counts (7.6): one result row per `player` seat **whose run
  began**: in personal pacing, a seat that was served at least one card; in shared pacing, a seat
  that was expected to answer in at least one round. An untouched forfeit therefore does not add a
  game, a win or a draw, as today.
- Wins, losses and draws follow each seat's own outcome in the standings.
- Sums (played, wins, losses, draws, points earned) are updated by addition. Streak handling is in
  5.5.
- Per-category and per-question accuracy count graded answers and recorded misses, not slots a
  player never reached.
- Question usage keeps today's two meanings: `asked` counts one per player answer or miss, which
  is what the admin screens show for trivia; `served` counts one per opened slot, which is what
  they show for poll questions.
- **The profile's total** is today's `TotalScore`: points earned less penalties, floored at zero.
  That figure is the all-time board's score, read through the `leaderboard.score` query. With
  `leaderboard` off, the profile shows points earned instead.

**`leaderboard`.** Each board and period keeps a ledger and a score derived from it.

- *Points.* A seat that is `ranked` adds one `points` entry per board (`all` and its format) and
  period (`all`, the month, the ISO week), dated at the session's end.
- *Abandonments.* A seat that is `eligible`, a `player` seat, marked abandoned by the format, in a
  session the format ended (never a core ending), is recorded once in `leaderboard_abandonments`,
  dated at the session's end like its points. Its amount is today's
  rule written out as fixed tiers: nothing for the first in seven days, then 200, 400, 800, and
  1000 for every one after. (Today's `LiveRules.AbandonmentPenalty` computes this with a shift
  that wraps around for very large counts; the tiers are what it means.) The seven days are counted over those rows, across week and month boundaries; equal
  times are ordered by session id.
- *Penalties.* An abandonment's amount is written as one `penalty` entry on each of the same
  boards and periods as that session's points.
- *The score* is the ledger folded in time order, and within one session its points before its
  penalty, as today's settlement applies them, adding points and subtracting penalties with a
  floor of zero **at each step**. A session's entries are always applied together. That is today's rule: a penalty can take a score to zero and no
  further, and the unpaid part is forgiven, not owed.
- *Order.* All of this runs under the player lock. An entry dated after everything already folded
  is applied directly. One dated earlier (events are unordered) makes the plugin recompute the
  amounts of the abandonments that follow it within seven days, update their penalty entries, and
  refold that player's affected scores from the ledger. The result is the same whatever order
  events arrive in. Today a result settled late is simply charged what the count was at that
  moment; computing from the true order of games is a deliberate change (19.4).
- Uses `friends.ids` for a friends board when available.

**`matchmaking`.** Each format that allows it declares its **match rule**; the two existing ones
keep today's behaviour:

| Format | Two players match when | What a waiting player has | Entry lives |
|---|---|---|---|
| `live` | same language and question count | only a queue entry; the first waiter's categories and levels are used | only while its player is online (9.6) |
| `async_duel` | same language | a real session already: its code, its drawn questions, its 48-hour clock, and they may already be playing it | up to 48 hours, online or not |

One waiting entry per player per format. Every change to an entry takes its player's lock first.
A pairing attempt first finds a candidate under the format's rule without locking, locks both
players in ascending id, and re-reads both entries. It gives up on a candidate, and removes the
entry, if the entry has expired or its player is banned or deleted; it skips one whose player
must be online and is not, or which another attempt holds a lease on.

**For `live`,** the rest is one transaction: delete both entries, requiring that exactly two rows
went; create the session **started**, with both players seated (7.3); bump both inboxes. If
anything fails it all rolls back: no entry is lost and no session exists.

**For `async_duel`,** the waiting player has an open session (7.3), and only that session's grain
may seat someone in it, so pairing takes three steps and carries one pairing token throughout:

1. In one transaction: delete the joiner's own entry, and put a 30-second lease on the waiting
   entry, marked with the token.
2. Call the session's grain to seat the joiner as that session's paired opponent. The call is
   idempotent for the same token.
3. In one transaction: delete the waiting entry, only if it still names that session and still
   carries that token.

If the outcome of step 2 is not known, the attempt first reads whether the joiner now has a seat
in that session, and continues from step 3 if so. If the join was refused or the lease ran out,
the waiting entry is offered again, or removed if its session can no longer be joined, and the
joiner is told to try again. A lease that has been taken over by another attempt is never deleted
by the first.

Because two players who enqueue at the same instant cannot see each other's
uncommitted entry, an attempt runs again right after an entry is committed, and a scheduled job
every few seconds pairs anything still waiting and removes entries that have expired.

**`ai_generation`.** Today's `TopUpQuestionBank` and `GenerateVotingQuestions`, run as jobs
instead of a grain, with their settings kept as the plugin's settings:

- a target per kind for each `(lang, category, level)` bucket (today 25 for choice, 6 for order,
  6 for map), where a bucket's stock counts approved **and** pending questions;
- a category is topped up only in languages it already has questions in;
- the nightly run is off by default; trivia results are auto-approved by default; poll questions
  are generated only on request and are pending by default;
- targeted and illustrated generation from the admin panel, and the choice of answer source for
  poll questions, are kept.

For each thin bucket it uses the kind's `IGenerates`, validates candidates with the kind's
post-checks and `Validate`, and stores the results. Each model call is three short
transactions, never one held open across the network. Every step changes the call's state and the
budget of the month the call was reserved against in the same transaction:

1. *Reserve:* the request is given a hard cap on output size, so its cost has a known upper
   bound. One conditional update succeeds only if spent plus reserved plus that bound stays within
   the month's limit; the call row is written as `reserved`.
2. *Send:* one conditional update moves the call from `reserved` to `sent`. Only the worker whose
   update changed the row may send the request.
3. *Settle:* one conditional update, valid only from `sent` or `unknown`, marks the call `done` or
   `failed`. From `sent` it moves the reserved bound out of "reserved" and adds the real cost to
   "spent". From `unknown` the bound has already been moved to "spent", so it only replaces that
   bound with the real cost.

Recovery: a call still `reserved` after 15 minutes becomes `expired` and can never be sent. A call
still `sent` well past the request timeout becomes `unknown`: its bound moves from "reserved" to
"spent" and it is never re-sent; the bucket is retried later as a new call. Because every
reservation is an upper bound, the month's limit cannot be exceeded; after a crash it can be
under-used.

**Not in the first build:** custom seasons, achievements, web push, daily challenges, teams,
tournaments, spaced repetition (8.4).

## 14. Background jobs (core)

One `jobs` table and a hosted worker service in every silo, running as `quesshi_system`:

- **Enqueue:** tenant code adds a job only through `enqueue_job()`, which stamps the current
  tenant. The core's own global work (account deletion, schedules) inserts directly as
  `quesshi_system`, with an explicit tenant or none. A job's identity for deduplication is (tenant,
  owner, kind, unique key): a second enqueue while the first is still waiting does nothing.
- **Claim:** one statement leases up to N ready jobs with `for update skip locked`, setting a
  random lease token and `locked_until`, and incrementing `attempts`.
- **Finish:** marking a job done, or rescheduling it with backoff, is conditional on the lease
  token.
- **A lease protects the bookkeeping, not the work.** A worker that overran its lease may still be
  running when another takes the job. What makes that safe is stated per job type: event handlers
  are exactly-once through `plugin_effects` (5.5); other jobs must be idempotent; mail may be sent
  twice in rare cases, which is accepted.
- **Give up:** after `max_attempts` a job becomes `dead` and is reported; an admin can re-run it.
  Clean-up jobs have no limit (11.1). A job whose plugin is off and says `Cancel` ends as
  `cancelled` without running.
- **Tenant work runs as the tenant.** A handler gets an `ITenantDb` and opens short transactions;
  it never holds one across a network call.
- **Schedules:** a plugin's schedule is enqueued with a unique key per time window, so several
  silos produce one job.

Jobs run mail, event handlers, plugin jobs, clean-up after deletion, media and guest clean-up and
expired sign-in rows. Session lifecycle (lobby expiry, forfeits, round ends) is **not** a job; it
uses `due_at` only.

## 15. HTTP API

```
GET  /api/stream?session=
GET  /api/me                      PUT /api/me                  GET /api/me/sessions?before=
POST /api/auth/code               POST /api/auth/verify        POST /api/auth/sign-out
POST /api/auth/guest              DELETE /api/me
GET  /api/config                  GET  /api/categories
GET|POST|PUT|DELETE /api/packs... /api/questions...

POST /api/sessions                {format, content, rules, capacity}
POST /api/sessions/join           {code}
GET  /api/sessions/{id}           PUT /api/sessions/{id}/settings
GET  /api/sessions/{id}/roster    GET  /api/sessions/{id}/effects
POST /api/sessions/{id}/start     POST /api/sessions/{id}/leave
POST /api/sessions/{id}/invites   POST /api/sessions/{id}/rematch
POST /api/sessions/{id}/commands

/api/x/{feature}/...              each feature plugin's player routes
/api/auth/x/{method}/...          each sign-in plugin's routes
/api/admin/...                    core admin: questions, categories, packs, media, players,
                                  capabilities, branding, admins, audit, dead jobs
/api/admin/x/{feature}/...        each feature plugin's admin routes
```

There are no per-format and no per-kind routes. This replaces `/api/matches/*`, `/api/live/*`,
`/api/voting/*`, `/api/admin/voting/*`, `/hub/live` and `/hub/lobby`. History is paged by
`(joined_at, session id)`.

### 15.1 What the admin panel does today, and who owns it afterwards

Every function the panel has now is kept. Roles: `owner` may do everything; `editor` may change
content; `moderator` may handle reports and players.

| Today's function | Owner afterwards | Least role |
|---|---|---|
| Dashboard counts | core | moderator |
| Thin buckets, generate now, targeted and illustrated generation, run history, AI spend | `ai_generation` | editor |
| Questions: list, search, create, edit, approve, reject, delete; per-kind forms | core content, with each kind's editor | editor |
| Poll questions and their generation | the same question screens, filtered by family; `ai_generation` | editor |
| Import with dry run and templates; media upload | core content, with each kind's import mapping | editor |
| Reported questions and dismissing reports | `reports` | moderator |
| Categories, and which categories each family may use | core content; the `bank` source's settings | editor |
| Players: list, search, ban | core identity | moderator |
| Live sessions: list active, end one | core sessions (the `admin` ending, 7.5) | moderator |
| Live on/off; lobby capacity limit | capability screen: the `live` format's enabled switch; a core setting | owner |
| Admin accounts: create, activate or deactivate this tenant's membership, reset, change password | core identity; global identity activation remains platform-admin-only | owner |
| New: capabilities, branding, audit log, dead jobs | core | owner |

## 16. Web serving and the Blazor client

- **The shell stays a static file.** `index.html` is the published, integrity-checked file the
  service worker caches, as today. It is not rewritten per tenant. What differs per tenant is
  fetched from three small routes that are resolved by host and are not part of the integrity
  manifest: `/theme.css` (the theme as CSS variables, linked from the shell), `/manifest.webmanifest`
  and `GET /api/config` (name, languages, text direction, enabled plugin ids, browser-safe
  settings for enabled sign-in methods, `configVersion`).
  - All three are served with an `ETag` and revalidated on every load, never cached blindly.
  - The service worker keeps the last good copy of each and serves it when offline, so the shell
    still paints with the right colours and direction.
  - When a pushed `configVersion` changes, the client re-reads the configuration and swaps the
    stylesheet link, so new branding appears without a reload.
  - The static join and landing pages pick language and direction by the same rule as the app:
    the visitor's saved choice, else the browser's language if the tenant has it, else the
    tenant's default. The app keeps the saved choice in the browser's storage today, which the
    server cannot see, so it also mirrors it into a plain `lang` cookie for the host. These pages
    are served with `Vary: Cookie, Accept-Language`.
- **Landing page:** rendered on the server per tenant and language (a Razor component rendered to
  static HTML), with no WebAssembly required. Today `Landing.razor` loads only after the runtime.
- **Invite links are the first-visit path that matters.** `/join/{code}` is answered by the server
  with a small static page: a generic per-tenant preview title, description and image for chat
  apps, and a "join" button. It does not look the lobby up. How the app takes over:
  - the page has two containers: `#preview`, filled by the server, and an empty `#app`;
  - the WebAssembly app starts loading at once and boots into `#app`, hidden;
  - pressing "join" before the app is ready only records the intent and shows a spinner; it sends
    nothing;
  - when the app is ready it removes `#preview`, shows itself, and makes the one join call. Joining
    twice is harmless, because a seat is unique per player.

  Tested for a signed-in player, a new guest, an expired code and a slow start.
- **First-load size:**
  - trimming and Brotli as published;
  - the admin area and the map kind's client (with the 150 KB world map) are lazy-loaded
    assemblies (section 4);
  - the service worker stops fetching every assembly and SVG at install, as
    `service-worker.published.js` does today: lazy assemblies and the map are left out of the
    install list and cached when first used.

  The target, measured on a mid-range phone over throttled mobile data with the service worker
  on, is a usable invite page in under one second and a playable app in under four.
- **Trimming:** plugin registries are explicit code, not assembly scanning, and JSON uses
  source-generated contexts, so the published, trimmed client is what the tests run against.
- **Streams:** response buffering and compression are off for the stream route; a keep-alive
  comment goes out every 20 seconds; the proxy must not buffer these routes and should speak
  HTTP/2 to browsers.

## 17. Configuration, deployment and operations

- **Environment only:** `ConnectionStrings__App` (as `quesshi_app`), `ConnectionStrings__System`
  (as `quesshi_system`), `SECRET_KEY`, `Smtp__*`, `MEDIA_DIR`, `TRUSTED_PROXIES`, plus whatever
  each plugin declares (for example the model provider's key). `quesshi migrate` takes the owner's
  connection string separately. Tenants, hosts, modes and rules leave `appsettings.json`.
- **Commands:** `serve` (the default), `migrate`, `sync`, `tenant add`, `admin add`. The existing
  `add-admin` and `approve-ai` commands are kept under the new names; `bench-matches` is replaced
  by the benchmark of 7.4.
- **First deployment shape:** one process (web host, silo, job worker and timer sweep) and
  PostgreSQL, as today minus Mongo and Redis. A release replaces the process; streams reconnect
  within a few seconds, grains re-activate from the database on the next command, and sessions
  continue.
- **The deployment pipeline changes, it is not reused as is.** `deploy/deploy.sh` and the compose
  file gain PostgreSQL: creating the three roles and extensions, running `migrate` before the
  switch, health checks that need the database, and the new secrets in place of the JWT keys.
  CI runs PostgreSQL instead of MongoDB. `SECRET_KEY` keys the code hashes; the antiforgery key
  ring is in the database (11.3).
- **Scaling up** is more silos. Orleans places grains across them; membership is in PostgreSQL.
  Nothing in the design changes; shared media storage is needed once there is more than one
  machine. The job worker can be switched off per silo by configuration, so heavy jobs can be kept
  away from silos that host live sessions.
- **Compatibility across releases.** The 90-day session limit (7.1) is what bounds all of this.
  - Working state: a format upgrades state step by step from any version an active session holds.
  - Play: a kind keeps a version's grading and its `Play` and `Reveal` components for 90 days
    after that version stops being drawn.
  - History: readers for stored cards, reveals, answers, rules and results are kept for ever.
  - Events: queued jobs carry identifiers only, so they survive a release unchanged.
  - Migrations are backward-compatible with the previous build for one release.
  - Grain interfaces: with one silo a release is a restart. With several, a rolling release
    requires grain interface changes to be additive for one release.
- **Health.** `/health` reports the database, clock calibration, silo membership, and the age of
  the last timer-sweep and job-loop pass; a stalled loop or an uncalibrated clock fails it.
- **Measured.** Timer lag, grain busy time per transition, command latency, on-time answers
  refused at close, stale-activation write rejections, job queue depth and age, dead jobs per
  plugin, stream count.
- **Backups.** PostgreSQL base backup with log archiving, plus the media directory. A restore is
  rehearsed before launch and verifies that every media row has its file.

## 18. Testing

- **Kinds and formats:** unit tests with fixtures and an in-memory `ISession`, including the
  non-interference test (6.3) and old-version snapshot upgrades. No grain, no database.
- **Plugin rules:** architecture tests for the reference and purity rules (section 4); catalogue
  tests that every tenant-owned table is registered, has the policy, and has no foreign key to
  another owner's plugin table; the lock-order check in the transaction wrapper is on in every test.
- **Isolation:** tests connect as `quesshi_app` and as `quesshi_system` and assert what each can
  and cannot read and write, table by table, including the column limits on tenant configuration.
- **The published client:** a trimmed, published build with the service worker on is loaded in a
  browser test: lazy assemblies load on demand, the stream connects, and a join link takes over.
- **Engine, on the Orleans test cluster with a real PostgreSQL:** simultaneous answers and several
  deliveries of the same timeout yield exactly one round close and consistent scores; a stale
  timeout does nothing; a retried `next` in a self-paced format advances once; a finished session
  rejects every further command; a session whose timeout keeps throwing ends as a fault without
  delaying others; **two activations of one session** (forced in the test) result in exactly one
  successful writer and the other deactivating; a failed transaction leaves memory and database
  agreeing after re-activation.
- **Events:** a handler interrupted after its writes and run again applies once; two workers
  running the same event apply once; a throwing handler does not affect other plugins or the
  session; clean-up runs for a disabled plugin and never before the player is marked deleted.
- **Timing:** the benchmark of 7.4; deadline lag with 5,000 open streams and fan-out active.
- **Delivery:** a dropped listening connection and a lost notification are repaired within the
  2-second reconcile; versions never go backwards at the client; a stream that stops accepting
  writes is disconnected.
- **Jobs:** a worker that overruns its lease cannot mark the job done after another has taken it.
- **Client:** bUnit tests render kind and format components from the shared fixtures; a slot
  contribution that throws does not break the screen.

What happens to today's tests: the domain tests for scoring, geography, fingerprints, password
rules, the question-set builder, live standings and voting results stay, moving with their code
into kinds, formats and sources. They are the specification that the formats keep today's rules.
The web tests of pure presentation helpers mostly stay. The grain and endpoint tests in
`Quesshi.Server.Tests` are tied to the three grains and are replaced by the engine and format
tests above; their scenarios are the checklist for the new ones.

## 19. From today's code to this design

### 19.1 What is kept, reshaped and removed

"Kept" means the code and its tests move with little change. "Adapted" means the idea and most of
the code survive but the shape changes.

| Today | Becomes | |
|---|---|---|
| `Scoring`, `MatchRules` and `LiveRules` constants | In `Domain` and the formats that use them | kept |
| `QuestionSetBuilder`, `CategorySelection` | The `bank` source | kept |
| `LiveMatch`'s round rules and standings; `VotingResults` | The `live` and `poll` formats | adapted |
| `MapTarget`, `Geo`, world map asset, projection, map components | The `map_pick` kind | kept |
| `SortOrder`, sort components | The `order` kind; display order derived differently (6.6) | adapted |
| `PromptFingerprint`, `TopicKey`, OpenRouter client, Wikipedia images | Core content and `ai_generation` | kept |
| `QuestionPromptBuilder` | `ai_generation`; language and category names resolved from tenant data (10.4) | adapted |
| Import pipeline and templates | Core, with per-kind column mapping; media URLs fetched into assets | adapted |
| Password policy, mail templates and senders, admin auth rules | Same, on PostgreSQL | kept |
| OTP checking | Rewritten as one atomic statement (11.2) | adapted |
| Admin panel pages | Same functions (15.1), new API, per-kind editors | adapted |
| i18n files, CSS, sounds, presentation helpers and their tests | Same | kept |
| Deployment scripts, compose file, CI | Gain PostgreSQL, lose Mongo and Redis (17) | adapted |
| `Question` + `VotingQuestion` + `QuestionDoc` | One envelope with a payload; six kinds | replaced |
| `Match`, `LiveMatch`, `VotingMatch` aggregates and snapshots | Three formats over one engine | replaced |
| `MatchGrain`, `LiveMatchGrain`, `VotingMatchGrain` | One `SessionGrain` | replaced |
| `GameEndpoints`, `LiveEndpoints`, `VotingEndpoints`, `Mappers` | One sessions API and one view renderer | replaced |
| `LiveHub`, `LobbyHub`, the three notifiers, `LiveClient`, `LobbyClient` | One stream and one `StreamClient` | replaced |
| `Play`, `Duel`, `Live`, `Voting`, `Lobby`, `Join`, `Home` pages | A shared shell and session page hosting format components | replaced |
| `Player` document | `accounts`, `players`, and plugin tables | replaced |
| `PlayerGrain`, `LiveMatchSettlement`, settlement checkpoints | Event handlers in `stats` and `leaderboard` | replaced |
| `MatchmakingGrain`, `LiveMatchmakingGrain` | The `matchmaking` plugin's table and pairing transaction | replaced |
| `LiveSettingsGrain`, `LobbySettingsGrain`, `ContentSettings`, `EnabledModes` | Capability rows and settings | replaced |
| `QuestionGeneratorGrain`, generation logs, spend log | `ai_generation` jobs and tables | replaced |
| `TenantContext` ambient tenant, `TenantGrainCallFilter`, database per tenant | Explicit tenant, row security | replaced |
| `Infrastructure/Mongo`, `Infrastructure/Redis` | PostgreSQL repositories | replaced |
| Orleans Redis clustering, persistence, reminders | PostgreSQL membership only | replaced |
| Two JWT schemes | Cookie sessions | replaced |
| `Language` enum, `NameFa`/`NameEn`/`NameNl` | Language codes, a names map | replaced |

### 19.2 How to get there: build beside, cut over once

An earlier draft of this plan changed the running application in place, stage by stage. Review
showed that path needs scaffolding that is then thrown away (grain state and reminders bridged
onto PostgreSQL, Redis services rewritten only to be deleted, old grains adapted to new question
shapes) and, worse, obliges every intermediate release to convert the production data the
previous one created. So the plan is different:

- **Production keeps running today's version** on MongoDB and Redis, unchanged except for fixes,
  until the new platform is complete.
- **The platform is a second solution in this repository,** `platform/Quesshi.Platform.slnx`,
  with its own projects (section 4), its own host, its own Blazor client, its own tests and its
  own CI job. Its code is merged into `main` as it is written. That is safe because nothing under
  `platform/` is built into or deployed with today's application until the cut-over, and the
  production deployment workflow is not changed before then. Today's `src/`, `tests/` and solution are not touched by it and keep building. The
  two never run in one process, so there are no shared routes, sign-in schemes, service
  registrations or silo settings to untangle.
- **Kept code is copied into the new tree** when a milestone needs it, with its tests. It is not
  moved, because today's projects still use it.
- **Fixes made to today's version meanwhile are tracked** in a short parity list, and each is
  applied to the copied code or noted as not applicable. Before the cut-over the list is checked
  against the latest production commit, not the commit the work started from.
- **Each milestone is deployed to a staging host** as its own tenant. Staging data may be reset
  between milestones.
- **Production moves once,** at the end, starting empty (19.3). The old tree is deleted in the
  cut-over release.

Milestones, each with what "done" means:

1. **Foundation.** The three roles and the migration runner; `ITenantDb` and the lock protocol;
   the clock; tenants, hosts and capabilities in the database with `sync`; the job runner and
   events; Orleans membership on PostgreSQL; the new host and a minimal Blazor shell that loads
   the tenant's theme and configuration; CI and deployment with PostgreSQL.
   *Done when:* staging serves the shell for two tenants on two hosts, and health checks pass.
2. **The live-path prototype and its benchmark** (7.4). The session tables; `SessionGrain` with
   the version-guarded write; the sweep; the stream and the real view renderer; the real `choice`
   kind's grading on production-sized questions; a minimal shared-pace format. It measures cold
   activation and a pool under load as well as the burst.
   *Done when:* the numbers are recorded and the player limit, or the need for batching, is
   decided. The same benchmark is run again before the cut-over with the finished formats and
   plugins.
3. **Identity.** Accounts, players, guests and the guest rules, `claim_account`, cookie sessions
   with antiforgery, the profile confirmation step, account deletion, admin accounts and the admin
   frame.
   *Done when:* a person can sign in on both staging tenants with one email, and an admin can
   sign in.
4. **Content.** The question envelope; the kind contract; `choice`, `order`, `map_pick`,
   `poll_choice`, `poll_roster` with their editors and play components; the `bank` source with
   the family allow-lists; media assets and the fetch policy; import; the admin question and
   category screens.
   *Done when:* an admin can author, import and approve every kind, and each kind's card renders.
5. **The engine and three formats.** Session creation; the full `SessionGrain`; the shared session
   page and lobby; `async_duel`, then `live`, then `poll`; invitations and rematch.
   *Done when:* all three formats are playable end to end on staging, with today's rule tests
   passing against the new formats.
6. **Feature plugins.** `stats` and `leaderboard` first, as the test of the plugin contract; then
   `friends`, `matchmaking`, `reports` with its admin screen, `ai_generation`; Google sign-in; the
   admin dashboard, live monitor and capability screens.
   *Done when:* every row of 15.1 works on staging.
7. **Flashcards.** Packs, the `pack` source, the `flashcard` kind and the `flashcards` format.
   Required before the cut-over: all four formats are in the first release, and this one is what
   proves the engine is not shaped around duels.
8. **Cut-over.** First-load work and its measurement (16); the question bank and media export
   and import (19.3); the benchmark again; a full rehearsal on staging, including a rehearsed
   roll-back; then production.

**Rolling back the cut-over.** For an agreed window afterwards, the complete previous deployment
is kept exactly as it was and out of reach of the new pipeline: the image, the compose file, its
environment and configuration with the old signing keys, the MongoDB and Redis volumes, and a
snapshot of the media folder. Rolling back means switching to it: the old application returns as
it was, and what was played on the new platform in between is lost. The switch back is rehearsed
on staging before the cut-over.

### 19.3 Existing data

Decision 1 (start empty) stands: PostgreSQL starts with no players, sessions or history.

The question bank is the exception worth a small tool, because regenerating it costs model spend.
A one-off script exports questions and categories from MongoDB into the import format, and copies
their media: uploaded files and Wikipedia-sourced images become media assets with their
attribution, and a question whose medium cannot be fetched is listed, not silently imported
without it. It is run in the rehearsal and again at the cut-over. It is not part of the product.

### 19.4 Deliberate differences from today

Everything else is meant to play as it does now. These are the intended changes:

| Change | Why |
|---|---|
| One account across tenants; a profile per tenant | Owner's decision |
| Sign-in by cookie session, not a stored token | Sessions can be revoked; needed for deletion and bans |
| A game's result is committed at once, as today; the stats and boards derived from it are updated a second or two later, exactly once | Replaces the multi-step settlement with retries |
| Abandonment penalties are computed from the true order of games, so a result processed late can correct the penalty of a later game | Today a late result is charged what the count happened to be |
| Every player who abandons a live game is penalised and credited no points for it | Today the penalty applies only when the whole game ends as abandoned, so someone who quits a game that others finish keeps their points and pays nothing |
| A player's accuracy counts only the rounds they were in | Today rounds played after they left are counted against them |
| In a tied async game, each player's record follows their own place | Today everyone gets a draw if anyone ties for first |
| The penalty for the fifth and later abandonment in a week stays at 1000 | Today's formula wraps around for very large counts |
| Points earned in a run that started as a guest never count on leaderboards, even after upgrading | Removes the back-fill and its races |
| Leaderboards gain per-format boards and monthly and weekly periods | New; the all-time board keeps today's meaning |
| A finished game's answers are stored as rows that can be queried and paged | Today they are kept in grain state, readable only by waking the grain |
| Every question medium is a stored file | Today a question may point at any URL |
| Languages are codes, not three fixed ones | Tenants may add languages |
| An unscoped bank draw excludes uncategorized questions | The owner's allow-list-only decision keeps every bank draw within active, family-allowed categories |
| Flashcards and packs | New format |

## 20. Decisions taken by the owner

1. **Start empty.** No players or history are carried over (the question bank: see 19.3).
2. **All four formats** (`async_duel`, `live`, `poll`, `flashcards`).
3. **One account across tenants,** with a player profile per tenant (11.1).
4. **Refactor Quesshi, keeping its stack:** .NET, Orleans and Blazor stay; PostgreSQL replaces
   MongoDB and Redis; server-sent events and POST replace SignalR.
5. **Plugins are the main way the system grows** (section 5).

6. **Build beside and cut over once** (19.2), instead of changing production in place stage by
   stage.
7. **Carry the question bank over** with a one-off export (19.3), while players and history start
   empty.
