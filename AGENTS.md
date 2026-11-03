# Agent guide for Quesshi

Read this before you change anything. Follow every **must** and **never** literally.

## What it is

One Bun process: Elysia (HTTP and WebSocket) in `server/`, features in `features/`, the Preact
client in `client/`, SQLite through `bun:sqlite`. Read [docs/design.md](docs/design.md) for the
model and [docs/operations.md](docs/operations.md) for running it.

## Commands

| Purpose | Command |
| --- | --- |
| Tests and typecheck (what CI runs) | `bun run check` |
| One test file | `bun test server/core/rooms.test.ts` |
| Build the client | `bun run build` |
| Run locally | `QUESSHI_SECRET=dev bun run dev` |
| Live load test | `bun scripts/bench-live.ts [players] [games] [questions]` |

`bun run check` must pass before you push.

## Layout

| Path | Contents |
| --- | --- |
| `server/core/` | Identity, admin, questions, rooms (lobbies and live delivery), sessions, migrations. |
| `server/app.ts` | Builds the app from the core and the listed features. |
| `features/<name>/` | One feature: its routes, kinds, formats, game actors, migrations and `client/`. |
| `features/index.ts`, `client/features.ts` | The hand-written lists that enable features. |
| `client/` | The core client: shell, router, sign-in, play hub, join, settings, admin. |
| `tests/support/` | `testApp` (the whole app on a temporary database with a fake clock), fixtures. |

## Rules

1. **The core never imports a feature; a feature never imports another.** If two features need
   each other, they become one. `tests/architecture.test.ts` enforces this.
2. **SQL only in `*.store.ts` files.** Elysia only in route files.
3. **Migrations are forward-only.** Add a new numbered file; never edit one that has shipped.
4. **Formats and kinds are pure:** no database, clock or network. Time arrives with each event.
5. **A game is written by its own actor only,** one command at a time; database transactions are
   synchronous with no `await` inside.
6. **Broadcasts stay small:** they go to every phone on every change, so they carry only what
   changes often (see `broadcastView`). A phone's first view and a read carry everything.
7. **A running game must survive a restart:** a game handle's `snapshot()` saves its whole state,
   and its room type's `resume()` rebuilds it.
8. **Keep today's UI:** reuse the classes in `client/public/css/app.css` and the translation keys
   in `client/public/i18n/`; render user text as text, never as HTML.

## Tests

- Write the test first. Test behaviour through the public interface; never reach private members.
- Fake only the edges: the clock and scheduler (`tests/support/time.ts`), mail, Google.
- No slow tests: no real waiting or sleeps; a timeout is only a hang guard.
- Ask whether a test adds value before adding it; one assertion per listed case.

## Style

TypeScript strict (`noUncheckedIndexedAccess`, `exactOptionalPropertyTypes`), no `any`, named
exports, `node:` imports. Comments explain why, in full sentences. Do only what is needed now.
This repository is public: never commit a real hostname, address, personal path or secret.
