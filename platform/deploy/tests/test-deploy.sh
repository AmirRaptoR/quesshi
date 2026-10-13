#!/usr/bin/env bash
set -euo pipefail

# This is an end-to-end deployment test. It uses PostgreSQL 18 and a unique Compose
# project/volumes, then proves success, host tenancy, no-op behaviour, and rollback.
readonly deploy_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly work_dir="$(mktemp -d)"
readonly test_id="${RANDOM}${RANDOM}"
readonly env_file="$work_dir/.env"
readonly state_dir="$work_dir/state"
readonly app_port="$((20000 + RANDOM % 10000))"
readonly postgres_port="$((40000 + RANDOM % 10000))"
readonly mailpit_port="$((50000 + RANDOM % 10000))"
readonly compose_project="quesshi-platform-deploy-test-$test_id"

cleanup() {
    PLATFORM_COMPOSE_PROJECT="$compose_project" \
        docker compose --project-directory "$deploy_dir" --env-file "$env_file" -f "$deploy_dir/compose.yaml" \
        down --volumes --remove-orphans >/dev/null 2>&1 || true
    rm -rf -- "$work_dir"
}
trap cleanup EXIT

sed \
    -e "s|^APP_PORT=.*|APP_PORT=$app_port|" \
    -e "s|^POSTGRES_PORT=.*|POSTGRES_PORT=$postgres_port|" \
    -e "s|^MAILPIT_UI_PORT=.*|MAILPIT_UI_PORT=$mailpit_port|" \
    -e "s|^MAILPIT_SMTP_PORT=.*|MAILPIT_SMTP_PORT=$((mailpit_port + 1))|" \
    -e "s|^POSTGRES_VOLUME=.*|POSTGRES_VOLUME=$compose_project-postgres|" \
    -e "s|^MEDIA_VOLUME=.*|MEDIA_VOLUME=$compose_project-media|" \
    -e "s|^DEPLOY_STATE_DIR=.*|DEPLOY_STATE_DIR=$state_dir|" \
    -e 's|^DEPLOY_HEALTH_TIMEOUT_SECONDS=.*|DEPLOY_HEALTH_TIMEOUT_SECONDS=20|' \
    -e 's|^DEPLOY_HEALTH_POLL_SECONDS=.*|DEPLOY_HEALTH_POLL_SECONDS=1|' \
    -e 's|^DEPLOY_POSTGRES_TIMEOUT_SECONDS=.*|DEPLOY_POSTGRES_TIMEOUT_SECONDS=60|' \
    -e 's|^DEPLOY_POSTGRES_POLL_SECONDS=.*|DEPLOY_POSTGRES_POLL_SECONDS=1|' \
    "$deploy_dir/.env.example" > "$env_file"

run_deploy() {
    PLATFORM_COMPOSE_PROJECT="$compose_project" DEPLOY_ENV_FILE="$env_file" DEPLOY_STATE_DIR="$state_dir" \
        "$deploy_dir/deploy.sh" "$1"
}

compose() {
    PLATFORM_COMPOSE_PROJECT="$compose_project" \
        docker compose --project-directory "$deploy_dir" --env-file "$env_file" -f "$deploy_dir/compose.yaml" "$@"
}

run_deploy good
compose exec -T postgres psql -U postgres -d quesshi_platform -tAc \
    "select count(*) from pg_roles where rolname in ('quesshi_owner', 'quesshi_app', 'quesshi_system')" \
    | grep -qx '3'
! compose exec -T app printenv ConnectionStrings__Owner >/dev/null 2>&1

RELEASE_TAG=good compose run --rm --no-deps app tenant add \
    --slug alpha --name Alpha --host alpha.staging.test --default-language en \
    --language 'en:English:English:ltr'
RELEASE_TAG=good compose run --rm --no-deps app tenant add \
    --slug beta --name Beta --host beta.staging.test --default-language en \
    --language 'en:English:English:ltr'
curl --fail --silent --show-error -H 'Host: alpha.staging.test' "http://127.0.0.1:$app_port/api/config" \
    | grep -q '"name":"Alpha"'
curl --fail --silent --show-error -H 'Host: beta.staging.test' "http://127.0.0.1:$app_port/api/config" \
    | grep -q '"name":"Beta"'
curl --fail --silent --show-error -H 'Host: alpha.staging.test' "http://127.0.0.1:$app_port/" \
    | grep -q '<!DOCTYPE html>'
curl --fail --silent --show-error -H 'Host: alpha.staging.test' \
    "http://127.0.0.1:$app_port/_framework/blazor.webassembly.js" >/dev/null
[[ "$(curl --silent --output /dev/null --write-out '%{http_code}' -H 'Host: missing.staging.test' "http://127.0.0.1:$app_port/api/config")" == 404 ]]

container_before="$(compose ps -q app)"
run_deploy good
[[ "$container_before" == "$(compose ps -q app)" ]]

sed -i 's|^APP_HEALTH_PATH=/health|APP_HEALTH_PATH=/not-healthy|' "$env_file"
if run_deploy bad; then
    printf 'a candidate with a failing health check unexpectedly deployed\n' >&2
    exit 1
fi
[[ "$(sed -n '1p' "$state_dir/current")" == good ]]
[[ "$(docker inspect --format '{{.Config.Image}}' "$(compose ps -q app)")" == quesshi-platform:good ]]
compose ps --status running app | grep -q app
