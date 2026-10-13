#!/bin/sh
set -eu

connection_value() {
    printf '%s' "$1" | sed 's/"/""/g'
}

connection_string() {
    printf 'Host="%s";Port="%s";Database="%s";Username="%s";Password="%s"' \
        "$(connection_value "$DB_HOST")" \
        "$(connection_value "$DB_PORT")" \
        "$(connection_value "$DB_NAME")" \
        "$(connection_value "$1")" \
        "$(connection_value "$2")"
}

: "${DB_HOST:?DB_HOST is required}"
: "${DB_PORT:?DB_PORT is required}"
: "${DB_NAME:?DB_NAME is required}"

if [ -n "${DB_APP_PASSWORD:-}" ]; then
    ConnectionStrings__App="$(connection_string quesshi_app "$DB_APP_PASSWORD")"
    export ConnectionStrings__App
fi
if [ -n "${DB_SYSTEM_PASSWORD:-}" ]; then
    ConnectionStrings__System="$(connection_string quesshi_system "$DB_SYSTEM_PASSWORD")"
    export ConnectionStrings__System
fi
if [ -n "${DB_OWNER_PASSWORD:-}" ]; then
    ConnectionStrings__Owner="$(connection_string quesshi_owner "$DB_OWNER_PASSWORD")"
    export ConnectionStrings__Owner
fi

unset DB_APP_PASSWORD DB_SYSTEM_PASSWORD DB_OWNER_PASSWORD
exec ./Quesshi.Server "$@"
