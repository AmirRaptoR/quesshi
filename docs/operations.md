# Running Quesshi

Quesshi is one Bun process with one SQLite file and a media folder. There is no other service to
run.

## Releases

Each release is one image, `ghcr.io/amirraptor/quesshi:<version>`, holding the server, the built
client and its production dependencies. The version lives in `package.json` (semantic versioning,
from 1.0.0), shows at the bottom of Settings and in the admin's account menu, and is reported by
`/api/health`.

To release: raise `version` in `package.json`, merge, then push a matching tag
(`git tag v1.0.1 && git push origin v1.0.1`). The Release workflow checks that the tag matches,
runs the tests and pushes the image.

To deploy a release on the production host, keeping its data folder and settings:

```sh
ssh <production host> 'bash -s' -- 1.0.1 < scripts/deploy.sh
```

It tries the image on a spare port first, switches only when the new one reports the expected
version, and puts the previous container back if the switch fails. To roll back, deploy an older
version. The host keeps `~/quesshi/env` (the settings below) and `~/quesshi/data`.

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
