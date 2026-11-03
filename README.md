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
```

- [docs/design.md](docs/design.md): how it is built and why.
- [docs/operations.md](docs/operations.md): settings, deploying, backups, importing today's questions.
- [AGENTS.md](AGENTS.md): the rules for changing it.

Licensed under the terms in [LICENSE](LICENSE).
