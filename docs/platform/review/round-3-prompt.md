Round 3, the final round. I changed docs/platform/architecture.md and docs/platform/schema.sql in response to your 36 round-2 findings. Re-read both files in full and re-check against the source in src/ where it matters. Still read-only: do not edit, create or delete any file.

How the round-2 findings were resolved (your numbers):

- 1: the started_at constraint has an explicit exception for a forfeit.
- 2, 3, 4: stale activation is its own outcome (drop everything, dispose the timer, deactivate, retryable error). The sweep never writes to a session: it keeps two in-memory sets (still calling, held back) and passes them to due_sessions(as_of, max, skip). Only the grain writes fault_count and retry_at, as a fenced write, and only for a format or kind bug (7.4, 7.5).
- 5, 6, 21: one leaderboard_abandonments row per player per session, dated at session end; the seven-day tier is counted over those rows across periods; its amount is projected onto the same boards and periods as the points. The profile total is the all-time board score through a named query. The true-order recomputation is listed as a deliberate change (13, 19.4).
- 7, 30: revision is carried through HTTP, the grain, the cache and the view; replacement updates the same row for a higher revision without counting again; same revision with a different answer is revision_conflict; "not applicable" is accepted for both poll kinds (8.1, 8.2).
- 8, 9, 10: creation has two shapes, a lobby or a started session with roster, snapshots and Start applied, in the caller's transaction. Live pairing uses the started shape in one transaction. Async pairing keeps today's waiting challenge: the entry points at the waiter's existing session, and joining is lease, grain call, delete. Revalidation checks expiry, bans and deleted players (7.3, 13).
- 11, 12, 23: the platform is a second solution under platform/ with its own host, client, tests and CI; kept code is copied, not moved; a parity list tracks production fixes; each milestone has a "done when"; session tables and the real renderer and kind are in the prototype milestone; report screens moved to the plugin milestone (19.2).
- 13, 26: an antiforgery token endpoint, refetched on every identity change, separate cookies and tokens for players and admins, one core filter for every route group; the key ring is stored in a database table (11.3, schema).
- 14: each lazy assembly registers its own components through an entry class with typeof references; the published-build test renders every component (5.2).
- 15: a fetch policy against internal destinations (12).
- 16: live keeps today's scheduled anchors; the engine no longer prescribes anchors (7.5).
- 17: stats only for seats whose run began; poll produces no player stats; question_stats.served keeps the usage counter (13).
- 18: a 16 KB limit on card plus reveal at approval and draw; finished views carry standings only and history is paged (9.3).
- 19: invitation and rematch rules as today; rematch is a format capability, today only live (7.3, 8.1).
- 20: advisory locks for the last-owner and last-platform-admin checks (11.2).
- 22: the prototype's required real components are named and the benchmark is repeated before cut-over.
- 24: the caller supplies the session id, so a retried create is idempotent; the grain is called after commit and never caches an absence.
- 25: formats reference a pure Engine.Contracts project that has no database interface (4).
- 27, 28: ETag-revalidated theme, manifest and config, kept by the service worker for offline, swapped on config change; the stream is shell-owned and app-wide (16, 9.1).
- 29: upgrading to an email that already has a player here is refused with AddressTaken, as today; eligibility is fixed at session start.
- 31: effects reports carry a database as-of time and the newest wins.
- 32, 36: QuestionPromptBuilder and OTP checking reclassified as adapted; 19.4 reworded.
- 33: seat exclusivity removed from the design and the schema.
- 34, 35: flashcards are required before cut-over; the rollback keeps the complete previous deployment and is rehearsed.

This is the last round, so I need two things:

A. Remaining problems, with the same precision and priority labels (P0–P3): round-2 findings still not resolved, new problems introduced, and any remaining rule of today's product that is dropped, changed without being listed in 19.4, or left undefined. Focus on what would cause a real bug, security hole, lost behaviour or rework if work started from these two documents as they are. Do not repeat resolved points and do not raise new stylistic or speculative points below P2.

B. A verdict. For each area say READY TO BUILD, READY WITH THE LISTED FIXES, or NOT READY, with one sentence:
   1. The plan: second solution, milestones, cut-over, rollback
   2. Tenancy, roles and row security
   3. Identity: accounts, players, guests, cookies, antiforgery, admins, deletion
   4. Question kinds and content (bank rules, media, import, generation)
   5. Session grain: fencing, outcomes, cache, creation
   6. Time: clock, eligibility, timers, sweep, faults, outage
   7. The formats and their fidelity to today's rules (async_duel, live, poll), and flashcards
   8. Delivery: stream, views, inbox, reconcile, effects
   9. Plugin model, events, clean-up, locking protocol
   10. The feature plugins (stats, leaderboard, friends, matchmaking, reports, ai_generation)
   11. Blazor client: registry, lazy loading, trimming, service worker, first load, join page
   12. The schema as SQL (never executed)

Output format: section A as numbered findings ordered by priority, each starting with its label, then where, problem, fix; a one-line count per priority. Section B as the twelve verdicts. End with the three things you would verify first in code before trusting this design.
