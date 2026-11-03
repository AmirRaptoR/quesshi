# Running Quesshi

Quesshi is one Bun process with one SQLite file and a media folder. There is no other service to
run.

## Releases

Every merge to `main` is a release. The Release workflow runs the tests, names the release after
the date (`20261103`, then `20261103.2`, `20261103.3` for later merges that day), publishes one
image, `ghcr.io/amirraptor/quesshi:<version>`, tags the commit `v<version>`, and deploys it to the
production host over SSH. The version shows at the bottom of Settings, in the admin's account
menu, and in `/api/health`.

The deploy (`scripts/deploy.sh`) tries the image on a spare port first, switches only when the new
one reports the expected version, puts the previous container back if the switch fails, and keeps
the last two images for a rollback. The host keeps `~/quesshi/env` (the settings below) and
`~/quesshi/data`.

To roll back, or to deploy by hand, run it with an earlier version:

```sh
ssh <production host> 'bash -s' -- 20261103 < scripts/deploy.sh
```

## Build and start from source

```sh
bun install --frozen-lockfile
bun run build          # builds the client into dist/client
bun start              # serves the API, the WebSockets and the client
```

Put it behind a reverse proxy that terminates TLS and passes WebSocket upgrades. The session
cookies are `Secure`, so the site must be served over HTTPS.

## Settings

| Variable | Required | Meaning |
| --- | --- | --- |
| `QUESSHI_SECRET` | yes | A long random value; it signs sign-in codes. Keep it stable. |
| `QUESSHI_ORIGIN` | yes in production | The site's public address, e.g. `https://quesshi.example.com`. Requests and sockets from any other origin are refused, and it is the base of admin reset links. |
| `QUESSHI_DATA_DIR` | no | Where `quesshi.sqlite` and `media/` live (default `data` in the checkout). |
| `PORT` | no | Port to listen on (default 3000). |
| `SMTP_HOST` | yes in production | Mail server for sign-in codes and admin reset links. Without it, mail is written to the log. |
| `SMTP_PORT` | no | 587 (STARTTLS) by default; 465 uses TLS from the start. |
| `SMTP_USER`, `SMTP_PASSWORD` | no | SMTP login, when the server needs one. |
| `SMTP_FROM` | no | The sender address (default `no-reply@quesshi.app`). |
| `SMTP_TLS` | no | `false` for a plain local relay. |
| `ADMIN_EMAIL` | first start | The first administrator's email; created only on an empty database. |
| `ADMIN_USERNAME` | no | The first administrator's name (default `admin`). |
| `ADMIN_PASSWORD` | no | Without it a password is generated, written to the log, and must be changed at the first sign-in. |
| `GOOGLE_CLIENT_ID` | no | Enables Google sign-in. |
| `TELEGRAM_BOT_TOKEN` | no | Enables sign-in from the Telegram Mini App; see [Telegram Mini App](#telegram-mini-app). |

## Telegram Mini App

Players can open Quesshi inside Telegram and are signed in with their Telegram account.

1. In Telegram, open [@BotFather](https://t.me/BotFather) and send `/newbot`; pick a name and a username. BotFather answers with the bot token.
2. Send `/newapp`, choose the bot, and give the app a title, description and icon. When it asks for the web app URL, enter the public origin, the same as `QUESSHI_ORIGIN` (e.g. `https://quesshi.example.com`, HTTPS only). Choose a short name; the app is then at `https://t.me/<bot>/<app>`.
3. Set `TELEGRAM_BOT_TOKEN` to the bot token and restart. Without it, Telegram sign-in is off.

The server checks the signature of the launch data with the token and refuses data older than 24 hours.
A link such as `https://t.me/<bot>/<app>?startapp=room_4821` opens the app straight into joining room `4821`.

## Restarts and deploys

Open lobbies and running games are saved as JSON snapshots in the database and come back after a
restart, so a deploy does not cancel anyone's game. A deadline that passed while the server was
down is applied straight away.

## Backups

Back up the data folder: `quesshi.sqlite` (with its `-wal` and `-shm` files) and `media/`. For a
consistent copy of a running database use `sqlite3 quesshi.sqlite ".backup backup.sqlite"`.

## Bringing over today's question bank

Where today's MongoDB runs, export the collections and copy the media folder next to them:

```sh
for c in categories content_settings questions; do
  mongoexport --db quesshi --collection $c --jsonArray --out $c.json
done
cp -r <today's wwwroot>/media ./media
```

Then, with Quesshi stopped: `bun scripts/import-legacy.ts <export folder> <data folder>`. It brings
categories, which categories trivia and polls offer, and every question today's rules still accept;
it lists what it skipped and why. Running it again adds only what is new.

## Load

`bun scripts/bench-live.ts [players] [games] [questions]` runs live games against a real server in
its own process and prints delivery times, memory and CPU.
