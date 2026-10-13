#!/usr/bin/env bash
set -euo pipefail

# Deploy one immutable platform image without handing migration credentials to the long-running app.
# The state directory deliberately lives outside a copied release tree: it retains the last known-good
# Compose settings and image tag required to undo a failed candidate.
readonly deploy_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly compose_file="$deploy_dir/compose.yaml"
readonly env_file="${DEPLOY_ENV_FILE:-$deploy_dir/.env}"
readonly release="${1:-${RELEASE_TAG:-}}"

fail() {
    printf 'Staging deployment error: %s\n' "$*" >&2
    exit 1
}

[[ -f "$compose_file" ]] || fail "missing Compose file: $compose_file"
[[ -f "$env_file" ]] || fail "missing staging settings: $env_file"
readonly state_dir="${DEPLOY_STATE_DIR:-$(sed -n 's/^DEPLOY_STATE_DIR=//p' "$env_file" | tail -n 1)}"
readonly health_timeout="${DEPLOY_HEALTH_TIMEOUT_SECONDS:-$(sed -n 's/^DEPLOY_HEALTH_TIMEOUT_SECONDS=//p' "$env_file" | tail -n 1)}"
readonly health_poll="${DEPLOY_HEALTH_POLL_SECONDS:-$(sed -n 's/^DEPLOY_HEALTH_POLL_SECONDS=//p' "$env_file" | tail -n 1)}"
readonly postgres_timeout="${DEPLOY_POSTGRES_TIMEOUT_SECONDS:-$(sed -n 's/^DEPLOY_POSTGRES_TIMEOUT_SECONDS=//p' "$env_file" | tail -n 1)}"
readonly postgres_poll="${DEPLOY_POSTGRES_POLL_SECONDS:-$(sed -n 's/^DEPLOY_POSTGRES_POLL_SECONDS=//p' "$env_file" | tail -n 1)}"
[[ "$release" =~ ^[A-Za-z0-9][A-Za-z0-9_.-]*$ ]] || fail 'pass a release tag containing only letters, digits, dot, underscore, or hyphen'
[[ "$state_dir" == /* ]] || fail 'DEPLOY_STATE_DIR must be an absolute path'
[[ "$health_timeout" =~ ^[0-9]+$ && "$health_poll" =~ ^[1-9][0-9]*$ ]] || fail 'health timeout and poll interval must be integer seconds'
[[ "$postgres_timeout" =~ ^[0-9]+$ && "$postgres_poll" =~ ^[1-9][0-9]*$ ]] || fail 'PostgreSQL timeout and poll interval must be integer seconds'
command -v docker >/dev/null || fail 'Docker with Compose is required'
command -v flock >/dev/null || fail 'flock is required'

compose() {
    docker compose --project-directory "$deploy_dir" --env-file "$env_file" -f "$compose_file" "$@"
}

snapshot_compose() {
    local snapshot="$1"
    shift
    docker compose --project-directory "$snapshot" --env-file "$snapshot/.env" -f "$snapshot/compose.yaml" "$@"
}

setting() {
    local key="$1" value
    value="$(sed -n "s/^${key}=//p" "$env_file" | tail -n 1)"
    [[ -n "$value" ]] || fail "$key must be set in $env_file"
    printf '%s' "$value"
}

readonly postgres_user="$(setting POSTGRES_USER)"
readonly postgres_database="$(setting POSTGRES_DB)"
readonly owner_password="$(setting DB_OWNER_PASSWORD)"
readonly app_password="$(setting DB_APP_PASSWORD)"
readonly system_password="$(setting DB_SYSTEM_PASSWORD)"

mkdir -p "$state_dir/releases"
chmod 700 "$state_dir" "$state_dir/releases"
readonly state_file="$state_dir/current"
readonly lock_file="$state_dir/deploy.lock"
exec 9>"$lock_file"
flock 9

compose config --quiet || fail 'Compose cannot load the staging settings'
config_fingerprint="$(sha256sum "$compose_file" "$env_file" | sha256sum | awk '{print $1}')"

current_release=''
current_fingerprint=''
if [[ -f "$state_file" ]]; then
    current_release="$(sed -n '1p' "$state_file")"
    current_fingerprint="$(sed -n '2p' "$state_file")"
    [[ "$current_release" =~ ^[A-Za-z0-9][A-Za-z0-9_.-]*$ && "$current_fingerprint" =~ ^[0-9a-f]{64}$ ]] \
        || fail "unreadable deployment state: $state_file"
fi

app_healthy() {
    local container status
    container="$(compose ps -q app 2>/dev/null || true)"
    [[ -n "$container" ]] || return 1
    status="$(docker inspect --format '{{.State.Health.Status}}' "$container" 2>/dev/null || true)"
    [[ "$status" == healthy ]]
}

wait_for_app() {
    local elapsed=0
    while (( elapsed <= health_timeout )); do
        app_healthy && return 0
        sleep "$health_poll"
        elapsed=$((elapsed + health_poll))
    done
    return 1
}

wait_for_postgres() {
    local elapsed=0 first_ready=0
    while (( elapsed <= postgres_timeout )); do
        if compose exec -T postgres psql -v ON_ERROR_STOP=1 -U "$postgres_user" -d "$postgres_database" \
            -tAc 'select 1' >/dev/null 2>&1; then
            # The official image briefly runs a temporary postmaster while it creates the
            # initial database. Require a second query after a poll before using it.
            (( first_ready )) && return 0
            first_ready=1
        else
            first_ready=0
        fi
        sleep "$postgres_poll"
        elapsed=$((elapsed + postgres_poll))
    done
    return 1
}

sql_literal() {
    sed "s/'/''/g" <<<"$1"
}

set_role_password() {
    local role="$1" password="$2"
    printf "alter role %s password '%s';\n" "$role" "$(sql_literal "$password")" \
        | compose exec -T postgres psql -v ON_ERROR_STOP=1 -U "$postgres_user" -d "$postgres_database" >/dev/null
}

save_snapshot() {
    local destination="$state_dir/releases/$release"
    local temporary="$destination.tmp.$$"
    rm -rf -- "$temporary"
    mkdir -p "$temporary"
    cp "$compose_file" "$temporary/compose.yaml"
    cp "$env_file" "$temporary/.env"
    chmod 600 "$temporary/.env"
    rm -rf -- "$destination"
    mv "$temporary" "$destination"
}

record_success() {
    local temporary="$state_file.tmp.$$"
    printf '%s\n%s\n' "$release" "$config_fingerprint" > "$temporary"
    chmod 600 "$temporary"
    mv "$temporary" "$state_file"
}

rollback() {
    if [[ -z "$current_release" ]]; then
        printf 'Candidate failed; no previously healthy release is recorded.\n' >&2
        RELEASE_TAG="$release" compose stop app >/dev/null || true
        return 0
    fi

    local previous="$state_dir/releases/$current_release"
    [[ -f "$previous/compose.yaml" && -f "$previous/.env" ]] \
        || fail "previous release configuration is missing: $previous"
    printf 'Candidate failed; restoring release %s.\n' "$current_release" >&2
    RELEASE_TAG="$current_release" snapshot_compose "$previous" up -d --no-build --no-deps --force-recreate app
    if ! wait_for_app; then
        fail "candidate failed and restored release $current_release did not become healthy"
    fi
}

# A healthy release under exactly the same configuration is a no-op. An unhealthy container must
# go through the candidate path instead of being falsely reported as deployed.
if [[ "$release" == "$current_release" && "$config_fingerprint" == "$current_fingerprint" ]] && app_healthy; then
    printf 'Release %s is already healthy; no replacement is needed.\n' "$release"
    exit 0
fi

compose up -d postgres mailpit
wait_for_postgres || fail "PostgreSQL did not become ready within ${postgres_timeout}s"

compose exec -T postgres psql -v ON_ERROR_STOP=1 -U "$postgres_user" -d "$postgres_database" \
    -f /dev/stdin < "$deploy_dir/bootstrap-roles.sql"
set_role_password quesshi_owner "$owner_password"
set_role_password quesshi_app "$app_password"
set_role_password quesshi_system "$system_password"

# Fail before touching the running application when a build, migration, or sync is invalid.
RELEASE_TAG="$release" compose build app
RELEASE_TAG="$release" compose run --rm --no-deps \
    -e "ConnectionStrings__Owner=Host=postgres;Port=5432;Database=$postgres_database;Username=quesshi_owner;Password=$owner_password" \
    app migrate
RELEASE_TAG="$release" compose run --rm --no-deps app sync

if ! RELEASE_TAG="$release" compose up -d --no-build --no-deps --force-recreate app; then
    rollback
    fail 'candidate application could not be started'
fi
if ! wait_for_app; then
    rollback
    fail "candidate release $release did not become healthy within ${health_timeout}s"
fi

save_snapshot
record_success
printf 'Release %s is healthy and deployed.\n' "$release"
