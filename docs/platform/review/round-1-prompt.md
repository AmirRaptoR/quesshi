You are reviewing a refactoring design before any code is changed. Do not edit, create or delete any file. Read only.

Files to review (this repo):
- docs/platform/architecture.md — the target design and the order of work
- docs/platform/schema.sql — the PostgreSQL 18 data model. It has never been executed (no Postgres here), so check the SQL itself.

The codebase being refactored is this repo: src/ and tests/ (.NET 10, ASP.NET Core, Orleans 10.2, Blazor WebAssembly, currently on MongoDB and Redis with SignalR). README.md and the other files in docs/ describe today's product rules. You must read the real source to judge the design: it claims to map today's code onto the target (section 19) and to keep today's rules.

Decisions already taken by the owner and not up for debate: refactor in place keeping .NET, Orleans and Blazor; PostgreSQL replaces MongoDB and Redis; server-sent events plus POST replace SignalR; one account across tenants with a player profile per tenant; four formats (async_duel, live, poll, flashcards); plugins (question kinds, game formats, features, sign-in methods, content sources) as the main way the system grows, with a small fixed core; no players or history carried over.

Background: the stack-independent parts of this design (schema, kind and format contracts, events with exactly-once markers, row-level security with three roles, endings, delivery by pushed views with a 2-second reconcile) were already reviewed in three rounds for a different stack and reworked. What is NEW and unreviewed is everything specific to this refactor: the Orleans session grain as single writer with a version-conditional write (7.4), grain timers plus a due_sessions sweep instead of reminders (7.5), reads never touching grains, the matchmaking queue grain (13), the .NET contracts (5.3, 6.2, 8.1), server-sent events in Blazor WebAssembly (9.1), cookie sessions replacing JWT (11.2), the solution layout and dependency rules (4), the mapping from today's code (19.1), the staged order of work with its bridge (19.2), and the first-load plan for Blazor (16).

Give a precise, adversarial review. For every finding: cite the section number, table/column, or source file and line; state the concrete failure scenario; give a concrete fix. No general advice.

Cover:

1. The Orleans execution model (7.4, 7.5, 7.6). Attack: the version-conditional write with duplicate activations; what the non-reentrant grain does to in-flight awaits, timers and the sweep; "copy, write, swap or deactivate" under partial failure (transaction committed but the response lost, commit outcome unknown); deactivating from inside a turn; activation cost and idle collection for 48-hour and 30-day sessions; the grain key carrying the tenant; the timer sweep calling grains from every silo; the outage and fault rules with grains; whether anything still needs Orleans reminders or grain storage. Check the 500-player live path against a non-reentrant grain doing one PostgreSQL transaction per answer. Is "reads never touch the grain" consistent with the caller's view being returned from the grain and with views rendered from the database possibly lagging?

2. Faithfulness to today's product rules. Read the source (src/Quesshi.Domain: Match.cs, LiveMatch.cs, VotingMatch.cs, Scoring.cs, MatchRules.cs, LiveRules.cs, VotingRules.cs, Question.cs, SortOrder.cs, DuelSettings.cs, Player.cs; src/Quesshi.Grains; src/Quesshi.Server/Api; docs/lobbies-and-n-player-duels.md, docs/sorting-and-map-questions.md) and list every rule the target design drops, changes or leaves undefined: lobby lifecycle, invitations, rematch, guests and upgrade, capacity limits, question count choices, difficulty ramp, category rotation, the live countdown/reveal timings, misses and abandonment, the async forfeit, voting specifics (answer sources, results, roster), exclusivity, bans, friends, presence, reports, media, generation targets and auto-approve, the admin panel's functions. For each, say whether the design covers it, contradicts it, or is silent.

3. The mapping and order of work (19). Is each stage really shippable with tests green? Attack the bridge in stage 1 (existing grains' JSON state in a PostgreSQL table through a custom storage class; SignalR without a backplane; Redis services reimplemented on PostgreSQL only to be deleted later), stage 3 changing kinds while three grains still exist, stage 4's one-format-at-a-time removal while the client has shared pages, and the temporary stats handler between stages 4 and 5. Name work the plan forgot, steps in the wrong order, and anything that would be thrown away. Check the "kept" column of 19.1 against the real code: is each item actually reusable as claimed?

4. .NET-specific risks: Npgsql + Dapper with composite keys, jsonb, citext, arrays and set_config in a transaction; two data sources and connection pool sizing with one transaction per grain command; LISTEN on a dedicated connection; Orleans ADO.NET clustering on PostgreSQL in its own schema with the three-role model; server-sent events from minimal APIs and consumption in Blazor WebAssembly with response streaming (reconnects, headers, cookies, back-pressure); cookie auth with SameSite and the PWA; DynamicComponent and lazy-loaded assemblies for plugin clients; trimming with reflection-based registries and System.Text.Json records; the architecture tests' ability to enforce the stated rules. Say where you are unsure rather than guessing.

5. The schema against this design: anything the Orleans model needs that the tables lack or that is now unnecessary (fault_count/retry_at, due_sessions, version, exclusive seats, matchmaking_entries with a queue grain), and any SQL problem you see.

6. Over-engineering: anything to cut or defer for a refactor of an existing, working product.

Output format — this matters:
- A numbered list of findings ordered by priority, highest first.
- Each finding starts with a priority label: P0 (the design is wrong or unimplementable as written; must fix before starting), P1 (will cause a real bug, security hole, lost product behaviour or rework; fix in the design now), P2 (should be decided or clarified before the affected stage is built), P3 (minor).
- Then: where; the problem in one or two sentences; the fix in one or two sentences.
- After the list, a one-line count per priority.
- End with a short list of what you checked and found sound.
Be complete rather than brief; do not pad.
