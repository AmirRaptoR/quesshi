# Quesshi

Trivia and polls to play with friends, in Persian, English and Dutch: live games for a room full of
phones, asynchronous duels, and polls about each other.

One Bun process serves everything: an Elysia HTTP and WebSocket server, a Preact app, and a SQLite
database next to a media folder.

```sh
bun install
bun run build                       # the client, into dist/client
QUESSHI_SECRET=dev bun run dev      # http://localhost:3000; sign-in codes are printed to the log
bun run check                       # tests and typecheck
bunx playwright install chromium    # once, for the browser tests
bun run e2e                         # builds the client, then runs the browser tests
```

The browser tests (`e2e/*.spec.ts`, Playwright, Chromium only) run at 360×740 once in Persian
(right to left) and once in English. Each test starts its own app on a free port with a fresh,
seeded temporary data directory and reads sign-in codes from the app's log. They are not part of
`bun run check`; CI runs them as the `e2e` job. Every new screen gets a phone-width check there.

- [docs/design.md](docs/design.md): how it is built and why.
- [docs/operations.md](docs/operations.md): settings, deploying, backups, importing today's questions.
- [AGENTS.md](AGENTS.md): the rules for changing it.

Licensed under the terms in [LICENSE](LICENSE).
