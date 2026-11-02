#!/bin/sh
# Refuses to let nginx start on a config the template step did not produce (issue #370, R2-N2).
#
# The stock 20-envsubst-on-templates.sh LOGS an unwritable output directory and returns 0 (verified against
# the pinned nginx-unprivileged:1.31-alpine image), and the Dockerfile deletes the base image's welcome
# default.conf. Without this check a read-only /etc/nginx/conf.d — the production overlay without its
# tmpfs — would start nginx with no site config: no CSP, no /api/ proxy, no security headers.
#
# It matches DIRECTIVES, not text, because the template's comments discuss the CSP too.
set -eu

ME=$(basename "$0")
conf="${NGINX_ENVSUBST_OUTPUT_DIR:-/etc/nginx/conf.d}/default.conf"

fail() {
    echo "$ME: map-config-unrendered: $1 (is /etc/nginx/conf.d writable? see docs/deployment.md, \"Location map\")" >&2
    exit 1
}

[ -f "$conf" ] || fail "the rendered site config is missing"

grep -Eq '^[[:space:]]*add_header[[:space:]]+Content-Security-Policy[[:space:]]' "$conf" \
    || fail "the rendered site config sets no Content-Security-Policy header"

if grep -Fq '${MAP_ALLOWED_ORIGINS}' "$conf"; then
    fail "the rendered site config still holds the unsubstituted map origin placeholder"
fi

grep -Eq '^[[:space:]]*location[[:space:]]+/api/[[:space:]]*\{' "$conf" \
    && grep -Eq '^[[:space:]]*proxy_pass[[:space:]]+http://api:8080/;' "$conf" \
    || fail "the rendered site config has no /api/ proxy"

exit 0
