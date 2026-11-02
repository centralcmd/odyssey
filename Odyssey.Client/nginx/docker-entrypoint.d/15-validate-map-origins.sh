#!/bin/sh
# Validates MAP_ALLOWED_ORIGINS before 20-envsubst-on-templates.sh writes it into the Content Security
# Policy (issue #370 §8). A non-zero exit stops the container before any config is rendered.
#
# This is the shell half of one rule; the .NET half is Odyssey.Api/Map/MapOriginRule.cs. Both run the
# vectors in TestShared/MapOriginVectors.json, so they cannot drift. Messages name the variable and the
# rule, never the value.
#
# The value is read straight from the environment — no word-splitting and no $(...) round-trip, which would
# strip trailing newlines and hide them — and control characters are rejected before anything else, so the
# whole-line anchoring of `grep -Ex` below is equivalent to the .NET pattern's \z.
set -eu

ME=$(basename "$0")

fail() {
    echo "$ME: map-csp-invalid: MAP_ALLOWED_ORIGINS $1" >&2
    exit 1
}

# Unset is a defect (the Dockerfile ENV guarantees a value); explicitly empty is the off-switch.
if [ -z "${MAP_ALLOWED_ORIGINS+set}" ]; then
    fail "is not set (set it to the tile origins, or to an empty value to admit none)"
fi

value=$MAP_ALLOWED_ORIGINS

# Any byte below 0x20, or 0x7F. Counted rather than captured, so a trailing newline cannot be stripped away.
all=$(printf '%s' "$value" | wc -c)
printable=$(printf '%s' "$value" | tr -d '\001-\037\177' | wc -c)
if [ "$all" -ne "$printable" ]; then
    fail "must not contain control characters (tab, carriage return, newline included)"
fi

[ -z "$value" ] && exit 0

case "$value" in
    " "* | *" ") fail "must not start or end with a space" ;;
    *"  "*) fail "must separate origins with single spaces" ;;
esac

count=0
set -f
IFS=' '
for entry in $value; do
    count=$((count + 1))
    if [ "$count" -gt 8 ]; then
        fail "must list at most 8 origins"
    fi

    if ! printf '%s\n' "$entry" | grep -Eqx 'https://[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*(:[0-9]{1,5})?'; then
        fail "entry $count must be a lowercase https origin with no path, wildcard or trailing slash"
    fi

    rest=${entry#https://}
    case "$rest" in
        *:*)
            port=${rest##*:}
            if [ "$port" -lt 1 ] || [ "$port" -gt 65535 ]; then
                fail "entry $count must have a port between 1 and 65535"
            fi
            ;;
    esac
done

exit 0
