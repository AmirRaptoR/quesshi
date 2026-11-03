#!/usr/bin/env bash
# Deploys a released image on the production host, keeping the data folder and settings:
#   ssh <host> 'bash -s' -- 20261103 < scripts/deploy.sh
# A failed start puts the previous container back. To roll back later, deploy an older version.
set -euo pipefail

VERSION=${1:?usage: deploy.sh <version>}
IMAGE=ghcr.io/amirraptor/quesshi:$VERSION
ROOT=${QUESSHI_ROOT:-$HOME/quesshi}
BIND=${QUESSHI_BIND:-127.0.0.1:5199}
TRIAL=${QUESSHI_TRIAL_BIND:-127.0.0.1:5198}

step() { printf '\n== %s\n' "$*"; }
# Waits until the health route on a host:port reports the expected version.
healthy() {
  for _ in $(seq 1 30); do
    [ "$(curl -sf "http://$1/api/health" | grep -o "\"version\":\"[^\"]*\"" || true)" = "\"version\":\"$VERSION\"" ] && return 0
    sleep 1
  done
  return 1
}
run() { docker run -d --name "$1" --restart unless-stopped --env-file "$ROOT/env" -v "$ROOT/data:/data" -p "$2:3000" "$IMAGE" >/dev/null; }

step "Pull $IMAGE"
docker pull -q "$IMAGE" >/dev/null

step "Data folder for the image's user"
docker run --rm --user root -v "$ROOT/data:/data" "$IMAGE" chown -R bun:bun /data

step "Trial run on $TRIAL"
docker rm -f quesshi-trial >/dev/null 2>&1 || true
run quesshi-trial "$TRIAL"
if ! healthy "$TRIAL"; then
  docker logs quesshi-trial 2>&1 | tail -20
  docker rm -f quesshi-trial >/dev/null
  echo "Trial failed; nothing changed."
  exit 1
fi
docker rm -f quesshi-trial >/dev/null

step "Switch $BIND to $VERSION"
docker rm -f quesshi-previous >/dev/null 2>&1 || true
if docker inspect quesshi >/dev/null 2>&1; then
  docker stop quesshi >/dev/null
  docker rename quesshi quesshi-previous
fi
run quesshi "$BIND"
if healthy "$BIND"; then
  docker rm -f quesshi-previous >/dev/null 2>&1 || true
  # Keep this release and the one before it for a rollback; older images only take disk.
  docker images ghcr.io/amirraptor/quesshi --format "{{.Tag}}" | tail -n +3 | while read -r old; do
    docker rmi "ghcr.io/amirraptor/quesshi:$old" >/dev/null 2>&1 || true
  done
  echo "Quesshi $VERSION is live."
else
  docker logs quesshi 2>&1 | tail -20
  docker rm -f quesshi >/dev/null
  if docker inspect quesshi-previous >/dev/null 2>&1; then
    docker rename quesshi-previous quesshi
    docker start quesshi >/dev/null
    echo "Start failed; the previous container is back."
  fi
  exit 1
fi
