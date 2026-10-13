# Platform staging deployment

This directory runs the isolated Platform milestone stack. It never shares the
production project's containers, host ports, database volume, or media volume.
It is designed for one private staging host behind Caddy.

## First deployment

1. Install Docker Engine with the Compose plugin and copy the release directory
   to the staging host.
2. Create a private settings file and state directory:

   ```sh
   cd platform/deploy
   cp .env.example .env
   chmod 600 .env
   sudo install -d -m 700 -o "$USER" /var/lib/quesshi-platform-staging
   ```

3. Edit `.env`. Replace every `replace-with-...` value, set the real
   `TRUSTED_PROXIES` address or CIDR, and keep the loopback bindings unless the
   host has an intentional private-network listener.
4. Build, migrate, synchronize capabilities, and start a release:

   ```sh
   ./deploy.sh milestone-1
   ```

`deploy.sh` waits for PostgreSQL, creates the three database roles if needed,
runs migrations with `quesshi_owner`, synchronizes with the application and
system credentials, then waits for `/health`. The normal app container never
receives the owner password. A failed candidate restores the last recorded
healthy image and its saved Compose settings.

For a known healthy release with unchanged `.env` and Compose file, repeating
the same command is a health-checked no-op:

```sh
./deploy.sh milestone-1
```

## Add a tenant host

Use the deployed image with the same settings. The host argument must be the
hostname that Caddy forwards, without a scheme or path:

```sh
RELEASE_TAG=milestone-1 docker compose --env-file .env -f compose.yaml run --rm --no-deps app \
  tenant add --slug games-staging --name "Games staging" \
  --host games-staging.example.test --default-language en \
  --language 'en:English:English:ltr'
```

Repeat it with another host to add another tenant. Requests for an unregistered
host return `404`; a registered host receives its own shell and configuration.

## Caddy and trusted proxies

Copy `Caddyfile.example` into the host Caddy configuration and replace
`staging.example.test`. Caddy terminates TLS and serves browser clients over
HTTP/2. It forwards the original `Host` header. The `/api/stream` handler turns
off response compression and buffering and uses immediate upstream flushing, so
stream frames are not held by the proxy.

Set `TRUSTED_PROXIES` in `.env` to the IP address or CIDR from which Caddy
connects to the app (usually `127.0.0.1` for the supplied loopback binding),
not to public client addresses. After reloading Caddy, verify the upstream sees
the requested tenant host and inspect a streaming endpoint with a client that
prints frames as they arrive; the first frame must arrive before the stream
finishes. Caddy's config validation is also part of Platform CI.

## Reset staging data

This deliberately clears the configured staging database and media volume
without reinitializing the PostgreSQL data directory, so `quesshi_owner`,
`quesshi_app`, and `quesshi_system` remain in the PostgreSQL catalog.

```sh
cd platform/deploy
set -eu
env_file="${DEPLOY_ENV_FILE:-.env}"
setting() { sed -n "s/^$1=//p" "$env_file" | tail -n 1; }
compose() { docker compose --env-file "$env_file" -f compose.yaml "$@"; }
postgres_user="$(setting POSTGRES_USER)"
postgres_database="$(setting POSTGRES_DB)"
media_volume="$(setting MEDIA_VOLUME)"
deploy_state_dir="$(setting DEPLOY_STATE_DIR)"
test -n "$postgres_user" && test -n "$postgres_database" && test -n "$media_volume" && test -n "$deploy_state_dir"
compose up -d --wait postgres
compose exec -T postgres \
  psql -v ON_ERROR_STOP=1 -U "$postgres_user" -d postgres -v database_name="$postgres_database" \
  <<'SQL'
SELECT format($sql$DROP DATABASE %I WITH (FORCE)$sql$, :'database_name') \gexec
SQL
compose exec -T postgres \
  psql -v ON_ERROR_STOP=1 -U "$postgres_user" -d postgres -v database_name="$postgres_database" \
  <<'SQL'
SELECT format($sql$CREATE DATABASE %I OWNER quesshi_owner$sql$, :'database_name') \gexec
SQL
compose down
docker volume rm "$media_volume"
rm -rf -- "$deploy_state_dir"
./deploy.sh "${RELEASE_TAG:-milestone-1}"
```

The next deployment reruns the idempotent role bootstrap and creates the
database schema and system records again. Removing the deployment state lets
the next deployment establish a new successful release. Keep the
`POSTGRES_*` bootstrap settings unchanged while retaining its data volume;
changing those settings requires recreating that staging volume separately.
