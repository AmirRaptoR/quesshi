#!/usr/bin/env bash
set -euo pipefail

readonly revision="${1:-HEAD}"
readonly commit="$(git rev-parse --verify "${revision}^{commit}")"
readonly description="$(git describe --tags --match 'v[0-9]*.[0-9]*.[0-9]*' --long "$commit")"

if [[ "$description" =~ ^v([0-9]+\.[0-9]+\.[0-9]+)-0-g[0-9a-f]+$ ]]; then
    version="${BASH_REMATCH[1]}"
elif [[ "$description" =~ ^v([0-9]+\.[0-9]+\.[0-9]+)-([1-9][0-9]*)-g([0-9a-f]+)$ ]]; then
    version="${BASH_REMATCH[1]}+${BASH_REMATCH[2]}.${BASH_REMATCH[3]}"
else
    printf 'Cannot derive a semantic version from release tags for %s: %s\n' "$commit" "$description" >&2
    exit 1
fi

printf 'QUESSHI_VERSION=%q\n' "$version"
printf 'QUESSHI_COMMIT=%q\n' "$commit"
