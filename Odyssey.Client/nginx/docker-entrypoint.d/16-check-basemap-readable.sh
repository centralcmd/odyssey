#!/bin/sh
# The self-hosted basemap's client-side start check (issue #384 §6, A9). The API validates the file at its own
# start, but runs as a different user: only this container can prove nginx (UID 101) can read it. Also proves
# the access-check cache can be created — the script runs before nginx, so it checks the PARENT directory,
# where nginx will create basemap_auth. Messages name the rule, never a path's contents.
set -eu

ME=$(basename "$0")
FILE=/usr/share/nginx/html/basemap/basemap.pmtiles
CACHE_PARENT=/var/cache/nginx

if [ ! -w "$CACHE_PARENT" ]; then
    echo "$ME: basemap-cache-unwritable: $CACHE_PARENT must be writable by nginx (a tmpfs owned by uid 101 in production)" >&2
    exit 1
fi

# No file is the ordinary case: the self-hosted styles are not selected, and /basemap/ answers 404.
if [ -e "$FILE" ] || [ -L "$FILE" ]; then
    if [ -L "$FILE" ] || [ ! -f "$FILE" ]; then
        echo "$ME: basemap-not-regular: the basemap file must be a regular file, not a symlink or a directory" >&2
        exit 1
    fi
    if [ ! -r "$FILE" ]; then
        echo "$ME: basemap-unreadable: nginx cannot read the basemap file; make it world-readable (0644)" >&2
        exit 1
    fi
fi
