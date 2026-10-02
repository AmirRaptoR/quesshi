#!/usr/bin/env bash
set -euo pipefail

readonly commit="${1:-}"
readonly run_number="${2:-}"
readonly run_attempt="${3:-}"
readonly deploy_root="${QUESSHI_DEPLOY_ROOT:-/opt/quesshi}"
readonly release_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly state_file="$deploy_root/.deployment-state"
readonly lock_file="$deploy_root/.deployment.lock"
readonly health_timeout="${QUESSHI_HEALTH_TIMEOUT_SECONDS:-180}"
readonly health_poll="${QUESSHI_HEALTH_POLL_SECONDS:-5}"

fail() {
    printf 'Deployment error: %s\n' "$*" >&2
    exit 1
}

[[ "$commit" =~ ^[0-9a-f]{40,64}$ ]] || fail 'expected a full commit SHA'
[[ "$run_number" =~ ^[1-9][0-9]*$ ]] || fail 'expected a positive GitHub run number'
[[ "$run_attempt" =~ ^[1-9][0-9]*$ ]] || fail 'expected a positive GitHub run attempt'
[[ "$health_timeout" =~ ^[0-9]+$ && "$health_poll" =~ ^[1-9][0-9]*$ ]] \
    || fail 'health timeout and poll interval must be integer seconds'

command -v docker >/dev/null || fail 'Docker with Compose is required'
command -v jq >/dev/null || fail 'jq is required'
command -v flock >/dev/null || fail 'flock is required'
mkdir -p "$deploy_root"
exec 9>"$lock_file"
flock 9

readonly compose_file="$release_dir/compose.yaml"
readonly env_file="$deploy_root/.env"
[[ -f "$compose_file" ]] || fail "release has no compose.yaml: $compose_file"
[[ -f "$env_file" ]] || fail "server settings are missing: $env_file"

compose_for() {
    local target_release="$1"
    shift
    docker compose --project-directory "$target_release" --env-file "$env_file" \
        -f "$target_release/compose.yaml" "$@"
}

compose() {
    compose_for "$release_dir" "$@"
}

config="$(compose config --format json)" || fail 'Compose could not load the production settings'
[[ "$(jq -r '.services.app.environment.ASPNETCORE_ENVIRONMENT // empty' <<<"$config")" == Production ]] \
    || fail 'ENVIRONMENT must be Production in the server .env'
smtp_host="$(jq -r '.services.app.environment["Smtp__Host"] // empty' <<<"$config")"
[[ -n "$smtp_host" && "$smtp_host" != mailpit ]] || fail 'SMTP_HOST must name a production mail server in the server .env'

config_file="$(jq -r '.services.app.volumes[] | select(.target == "/app/appsettings.Production.json") | .source' <<<"$config")"
media_uploads="$(jq -r '.services.app.volumes[] | select(.target == "/app/wwwroot/media/uploads") | .source' <<<"$config")"
media_sourced="$(jq -r '.services.app.volumes[] | select(.target == "/app/wwwroot/media/sourced") | .source' <<<"$config")"
[[ -n "$config_file" && -f "$config_file" ]] || fail 'production appsettings file is missing'
[[ -n "$media_uploads" && -d "$media_uploads" ]] || fail 'uploaded media directory is missing'
[[ -n "$media_sourced" && -d "$media_sourced" ]] || fail 'sourced media directory is missing'
[[ "$config_file" == /* && "$media_uploads" == /* && "$media_sourced" == /* ]] \
    || fail 'production settings and media paths must be absolute server paths'
jq -e 'type == "object" and (.Jwt.Key | type == "string" and length > 0) and (.AdminAuth.Key | type == "string" and length > 0)' \
    "$config_file" >/dev/null || fail 'production settings must contain Jwt.Key and AdminAuth.Key'

mongo_volume="$(jq -r '.services.mongo.volumes[0].source as $key | .volumes[$key].name // $key' <<<"$config")"
redis_volume="$(jq -r '.services.redis.volumes[0].source as $key | .volumes[$key].name // $key' <<<"$config")"
[[ -n "$mongo_volume" && -n "$redis_volume" ]] || fail 'Compose must define named Mongo and Redis volumes'
docker volume inspect "$mongo_volume" >/dev/null || fail "Mongo volume does not exist: $mongo_volume"
docker volume inspect "$redis_volume" >/dev/null || fail "Redis volume does not exist: $redis_volume"

if [[ -f "$state_file" ]]; then
    previous_commit="$(jq -er '.commit | strings' "$state_file")" || fail 'deployment state is unreadable'
    stored_previous_commit="$(jq -r '.previous_commit // empty' "$state_file")" \
        || fail 'deployment state is unreadable'
    previous_run="$(jq -er '.run_number | numbers' "$state_file")" || fail 'deployment state is unreadable'
    previous_attempt="$(jq -er '.run_attempt | numbers' "$state_file")" || fail 'deployment state is unreadable'
    if (( run_number < previous_run || (run_number == previous_run && run_attempt < previous_attempt) )); then
        fail "stale run $run_number/$run_attempt; current release is $previous_run/$previous_attempt ($previous_commit)"
    fi
    if (( run_number == previous_run && run_attempt == previous_attempt )); then
        [[ "$commit" == "$previous_commit" ]] || fail 'run metadata was already used for a different commit'
        printf 'Commit %s is already deployed.\n' "$commit"
        exit 0
    fi
else
    previous_commit=''
fi

previous_image_id=''
if docker inspect --format '{{.Image}}' quesshi-app >/dev/null 2>&1; then
    previous_image_id="$(docker inspect --format '{{.Image}}' quesshi-app)"
fi

printf 'Building tested commit %s.\n' "$commit"
TAG="$commit" compose build app || fail 'image build failed; the running app was left untouched'

rollback_image="quesshi:rollback-$commit"
rollback() {
    if [[ -n "$previous_image_id" ]]; then
        previous_release="$release_dir"
        if [[ -n "$previous_commit" ]]; then
            previous_release="$deploy_root/releases/$previous_commit"
            [[ -f "$previous_release/compose.yaml" ]] \
                || fail "previous release definition is missing: $previous_release/compose.yaml"
        fi
        docker tag "$previous_image_id" "$rollback_image" || fail 'could not tag the previous app image for rollback'
        if ! TAG="rollback-$commit" compose_for "$previous_release" up -d --no-deps app; then
            fail 'new app failed and Compose could not restore the previous image'
        fi
        if ! wait_for_healthy; then
            fail 'new app failed and the restored app did not become healthy'
        fi
        printf 'Restored the previous application image.\n' >&2
    else
        TAG="$commit" compose stop app || true
        printf 'No previous application image was available to restore.\n' >&2
    fi
}

wait_for_healthy() {
    local elapsed=0 status=''
    while (( elapsed <= health_timeout )); do
        status="$(docker inspect --format '{{.State.Health.Status}}' quesshi-app 2>/dev/null || true)"
        [[ "$status" == healthy ]] && return 0
        sleep "$health_poll"
        elapsed=$((elapsed + health_poll))
    done
    printf 'Application health status after %s seconds: %s\n' "$health_timeout" "${status:-unavailable}" >&2
    return 1
}

if ! TAG="$commit" compose up -d --no-deps app; then
    printf 'Application switch failed.\n' >&2
    rollback
    exit 1
fi
if ! wait_for_healthy; then
    printf 'New application did not pass its /health container check.\n' >&2
    rollback
    exit 1
fi

state_previous_commit="$previous_commit"
if [[ "$commit" == "$previous_commit" ]]; then
    state_previous_commit="$stored_previous_commit"
fi
state_tmp="$state_file.tmp.$$"
jq -n --arg commit "$commit" --arg previous_commit "$state_previous_commit" \
    --argjson run_number "$run_number" --argjson run_attempt "$run_attempt" \
    '{commit:$commit, previous_commit:(if $previous_commit == "" then null else $previous_commit end), run_number:$run_number, run_attempt:$run_attempt}' > "$state_tmp"
chmod 600 "$state_tmp"
mv -f "$state_tmp" "$state_file"
printf 'Commit %s is healthy and deployed.\n' "$commit"

# Keep only what a rollback can still use: this release and the one recorded as previous. Anything
# else is a full source tree plus an image on a disk shared with other apps. Hand-tagged images
# (timestamps, "local") and in-flight .incoming transfers are not ours to remove. A failed prune
# leaves extra files behind but never fails a release that is already live.
is_kept() {
    [[ "$1" == "$commit" || ( -n "$state_previous_commit" && "$1" == "$state_previous_commit" ) ]]
}
for old_release in "$deploy_root"/releases/*; do
    name="$(basename "$old_release")"
    [[ -d "$old_release" && "$name" =~ ^[0-9a-f]{40,64}$ ]] || continue
    is_kept "$name" || rm -rf -- "$old_release" || printf 'Could not remove old release %s.\n' "$name" >&2
done
while read -r tag; do
    [[ "$tag" =~ ^(rollback-)?([0-9a-f]{40,64})$ ]] || continue
    [[ -z "${BASH_REMATCH[1]}" ]] && is_kept "${BASH_REMATCH[2]}" && continue
    docker image rm "quesshi:$tag" >/dev/null || printf 'Could not remove old image quesshi:%s.\n' "$tag" >&2
done < <(docker image ls quesshi --format '{{.Tag}}' || true)
