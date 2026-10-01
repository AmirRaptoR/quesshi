# Production deployment

Production deploys automatically after the existing **Build and test** job succeeds for a push to
`main`. Pull requests and failed builds do not receive deployment credentials or run the deploy job.
GitHub Actions sends the archive for that exact commit over SSH, then runs that commit's
`deploy/deploy.sh` on the Compose server. Deployment runs are serialized, and the server rejects a
run number older than the last successful release.

## GitHub repository secrets

Add these four repository secrets before enabling deployment:

| Secret | Value |
| --- | --- |
| `DEPLOY_HOST` | Server DNS name or IP address, matching the host entry in `DEPLOY_KNOWN_HOSTS`. |
| `DEPLOY_USER` | SSH account that owns `/opt/quesshi` and can run Docker Compose. |
| `DEPLOY_SSH_KEY` | Private key for that account. Use a dedicated key restricted to this server. |
| `DEPLOY_KNOWN_HOSTS` | The server's pinned `known_hosts` line, obtained and verified through a trusted channel. |

The workflow uses `StrictHostKeyChecking=yes`; it never discovers and trusts a host key at deploy
time. Rotate the pinned line when the server's SSH host key is intentionally changed. Secrets are
only available to the main-branch deployment job and are not printed by the workflow.

## Server setup

Prepare a Linux server with Docker Engine, the Docker Compose plugin, `bash`, `jq`, `flock`, and
`tar`. The deployment account must be able to build and run containers and write `/opt/quesshi`.
Create `/opt/quesshi/releases` and keep the following server-managed files and directories outside
release archives:

- `/opt/quesshi/.env`, readable by the deployment account and mode `0600`.
- `/opt/quesshi/appsettings.Production.json`, readable only by the deployment account and the
  services that need it.
- `/opt/quesshi/data/uploads` and `/opt/quesshi/data/sourced`, writable by the application UID
  and GID configured in `.env` (defaults to `1000:1000`).
- The existing named Mongo and Redis Docker volumes configured by `MONGO_VOLUME` and
  `REDIS_VOLUME` in `.env` (defaults: `quesshi-mongo` and `quesshi-redis`).

Set at least these values in `.env`:

```dotenv
ENVIRONMENT=Production
CONFIG_FILE=/opt/quesshi/appsettings.Production.json
MEDIA_DIR=/opt/quesshi/data
SMTP_HOST=smtp.example.com
SMTP_PORT=587
SMTP_TLS=true
MONGO_VOLUME=quesshi-mongo
REDIS_VOLUME=quesshi-redis
```

The production settings file must be valid JSON and contain nonempty `Jwt.Key` and `AdminAuth.Key`
secrets. The preflight also renders `compose.yaml`, confirms production mail configuration, checks
both media paths and the settings file, and verifies that the configured named database volumes
already exist. Any missing requirement stops the release before building or switching the app.
Production configuration and media are mounted from these persistent server paths; deployment
builds and replaces only the `app` service. It never runs Compose `down`, recreates Mongo or Redis,
or removes a named volume.

## Health checks, failures, and recovery

The deployment builds the tested commit as `quesshi:<commit-sha>`, switches only the app container,
and waits for the Compose `/health` check to become healthy before recording success. A build error
leaves the current app untouched. If the switch or health check fails, the script restores the
previous running image and exits nonzero. Previous commit images and release archives are retained.

Diagnose a failed run in the GitHub Actions job log first. On the server, inspect
`/opt/quesshi/.deployment-state`, check the app with `docker compose logs app`, and fix any reported
preflight issue in the server-managed files or Docker volumes before retrying. A stale-run error
means a newer run has already deployed; rerun CI from the current `main` head rather than replaying
an older archive.

To restore the release that preceded the current successful release, run as the deployment account
on the server. This takes the same lock as automatic deployment, selects the last known good image,
waits for its health check, and updates the stored deployed commit only after it is healthy:

```bash
set -euo pipefail
cd /opt/quesshi
exec 9>.deployment.lock
flock 9
CURRENT=$(jq -r .commit .deployment-state)
PREVIOUS=$(jq -r '.previous_commit // empty' .deployment-state)
test -n "$PREVIOUS"
RELEASE="/opt/quesshi/releases/$CURRENT"
TAG="$PREVIOUS" docker compose --project-directory "$RELEASE" --env-file /opt/quesshi/.env \
  -f "$RELEASE/compose.yaml" up -d --no-deps app
healthy=0
for attempt in {1..24}; do
  status=$(docker inspect --format '{{.State.Health.Status}}' quesshi-app 2>/dev/null || true)
  if [ "$status" = healthy ]; then healthy=1; break; fi
  sleep 5
done
[ "$healthy" = 1 ] || { echo "Previous app did not become healthy" >&2; exit 1; }
jq --arg commit "$PREVIOUS" --arg previous_commit "$CURRENT" \
  '.commit = $commit | .previous_commit = $previous_commit' .deployment-state \
  > .deployment-state.tmp
chmod 600 .deployment-state.tmp
mv .deployment-state.tmp .deployment-state
```

The normal CI run sequence remains recorded during recovery, so a delayed older workflow still
cannot undo it. After configuring the server and secrets, the owner should confirm that a successful
`main` run updates the live deployment and that its public `/health` endpoint remains healthy.
