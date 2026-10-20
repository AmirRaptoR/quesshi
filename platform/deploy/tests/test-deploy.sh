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
    -e 's|^DB_OWNER_PASSWORD=.*|DB_OWNER_PASSWORD=owner;with:semicolon|' \
    -e 's|^DB_APP_PASSWORD=.*|DB_APP_PASSWORD=app;with:semicolon|' \
    -e 's|^DB_SYSTEM_PASSWORD=.*|DB_SYSTEM_PASSWORD=system;with:semicolon|' \
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
docker volume inspect "$compose_project-media" >/dev/null
! compose exec -T app printenv ConnectionStrings__Owner >/dev/null 2>&1
! compose exec -T app sh -c 'tr "\\0" "\\n" </proc/1/environ | grep -q "^ConnectionStrings__Owner="'
app_connection="$(compose exec -T app sh -c 'tr "\\0" "\\n" </proc/1/environ | sed -n "s/^ConnectionStrings__App=//p"')"
[[ "$app_connection" == 'Host="postgres";Port="5432";Database="quesshi_platform";Username="quesshi_app";Password="app;with:semicolon"' ]] \
    || { printf 'app connection string did not preserve its quoted password: %s\n' "$app_connection" >&2; exit 1; }

container_before="$(compose ps -q app)"
sed -i 's|^DB_APP_PASSWORD=.*|DB_APP_PASSWORD=rotated;with:semicolon|' "$env_file"
if run_deploy rotated; then
    printf 'a stateful credential change unexpectedly replaced the healthy release\n' >&2
    exit 1
fi
[[ "$container_before" == "$(compose ps -q app)" ]]
[[ "$(docker inspect --format '{{.State.Health.Status}}' "$container_before")" == healthy ]]
sed -i 's|^DB_APP_PASSWORD=.*|DB_APP_PASSWORD=app;with:semicolon|' "$env_file"

RELEASE_TAG=good compose run --rm --no-deps app tenant add \
    --slug alpha --name Alpha --host alpha.staging.test --default-language en \
    --language 'en:English:English:ltr'
RELEASE_TAG=good compose run --rm --no-deps app tenant add \
    --slug beta --name Beta --host beta.staging.test --default-language en \
    --language 'en:English:English:ltr'
alpha_config="$(curl --fail --silent --show-error -H 'Host: alpha.staging.test' "http://127.0.0.1:$app_port/api/config")"
[[ "$alpha_config" == *'"name":"Alpha"'* ]]
beta_config="$(curl --fail --silent --show-error -H 'Host: beta.staging.test' "http://127.0.0.1:$app_port/api/config")"
[[ "$beta_config" == *'"name":"Beta"'* ]]
alpha_page="$(curl --fail --silent --show-error -H 'Host: alpha.staging.test' "http://127.0.0.1:$app_port/")"
[[ "$alpha_page" == *'<!DOCTYPE html>'* ]]
curl --fail --silent --show-error -H 'Host: alpha.staging.test' \
    "http://127.0.0.1:$app_port/_framework/blazor.webassembly.js" >/dev/null
[[ "$(curl --silent --output /dev/null --write-out '%{http_code}' -H 'Host: missing.staging.test' "http://127.0.0.1:$app_port/api/config")" == 404 ]]

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

sed -i 's|^APP_HEALTH_PATH=/not-healthy|APP_HEALTH_PATH=/health|' "$env_file"
compose exec -T app touch /app/wwwroot/media/pre-reset

# Execute the documented reset rather than duplicating its commands, so this test
# proves that the operator instructions clear data while retaining PostgreSQL roles.
reset_script="$work_dir/reset-staging.sh"
awk '
    /^## Reset staging data$/ { reset_section = 1; next }
    reset_section && /^```sh$/ { code_block = 1; next }
    code_block && /^```$/ { exit }
    code_block { print }
' "$deploy_dir/README.md" > "$reset_script"
(
    cd "$(cd "$deploy_dir/../.." && pwd)"
    PLATFORM_COMPOSE_PROJECT="$compose_project" DEPLOY_ENV_FILE="$env_file" DEPLOY_STATE_DIR="$state_dir" RELEASE_TAG=good \
        bash "$reset_script"
)

! docker volume inspect "$compose_project-media" >/dev/null 2>&1
compose up -d postgres
role_count="$(compose exec -T postgres psql -U postgres -d quesshi_platform -tAc \
    "select count(*) from pg_roles where rolname in ('quesshi_owner', 'quesshi_app', 'quesshi_system')")"
[[ "$role_count" == 3 ]] || { printf 'reset did not retain all database roles: %s\n' "$role_count" >&2; exit 1; }
tenant_count="$(compose exec -T postgres psql -U postgres -d quesshi_platform -tAc \
    'select count(*) from tenants')"
[[ "$tenant_count" == 0 ]] || { printf 'reset did not clear staged tenants: %s remain\n' "$tenant_count" >&2; exit 1; }
compose down
run_deploy good
curl --fail --silent --show-error "http://127.0.0.1:$app_port/health" >/dev/null
docker volume inspect "$compose_project-media" >/dev/null
