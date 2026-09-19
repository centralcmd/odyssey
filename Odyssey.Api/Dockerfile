# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine@sha256:3cc3bbbbf93d82104892f42aa9106b6be4d120346dea0649643a97c801525256 AS build

COPY . /src
WORKDIR /src
# An OPTIONAL extra CA for the restore, for environments whose egress is TLS-intercepted.
#
# A build container inherits an intercepting proxy but not its CA, so `dotnet restore` dies on
# NU1301 UntrustedRoot several minutes in — a trust failure wearing the costume of a NuGet outage,
# made stranger by image pulls and Testcontainers continuing to work (the daemon holds the CA).
#
# The secret is optional: with no `--secret id=proxy_ca` the mount is absent, the guard is false,
# and this is exactly the plain `dotnet restore` it replaces. CI and an ordinary developer machine
# take that path and are unaffected — which is the point, since the alternative considered was
# COPYing a CA in, and a session-local CA baked into a published image is worse than the problem.
#
# Nothing persists either way. The CA arrives on a secret mount and the merged bundle is assembled
# in a tmpfs; BuildKit discards both with the RUN, so no layer here carries the CA. SSL_CERT_FILE
# is exported inside this RUN's shell rather than as an ENV, so it cannot reach a later stage —
# and .NET reads it through OpenSSL, which is why no `update-ca-certificates` (absent from the
# alpine SDK image anyway) and no write to /etc/ssl are needed.
RUN --mount=type=secret,id=proxy_ca \
    --mount=type=tmpfs,target=/tmp/ca \
    if [ -s /run/secrets/proxy_ca ]; then \
        cat /etc/ssl/certs/ca-certificates.crt /run/secrets/proxy_ca > /tmp/ca/bundle.crt && \
        export SSL_CERT_FILE=/tmp/ca/bundle.crt; \
    fi; \
    dotnet restore
RUN dotnet publish Odyssey.Api/Odyssey.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine@sha256:f62a272ac1b46e83f56b8ed0416572f31cd1128e2c4a5e63eb34d348e4a36095 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Globalization (issue #48). The alpine runtime image ships no ICU, so .NET falls back to INVARIANT
# globalization — under which CompareOptions.IgnoreNonSpace silently stops folding accents. That is
# the comparison the contact-alias duplicate check uses to agree with the column's utf8mb4_*_ci unique
# index, so without ICU the service would accept "Renee" against a stored "Renée" and let the INDEX
# reject it: a 500-shaped failure on an ordinary write, reproducible nowhere else — a dev box and both
# fast test tiers have ICU. Installed here, asserted at startup by GlobalizationGuard, and the
# environment variable is pinned false so a base-image change cannot silently re-enable invariant mode.
# The same optional CA as the restore above, for the same reason — apk speaks TLS too, and an
# intercepted egress fails this with `TLS: server certificate not trusted`, which apk then reports
# as `unable to select packages` (a missing-package error for a trust problem).
#
# This is the RUNTIME stage, so unlike the build stage nothing here may persist: these layers ship.
# It stays clean because apk honours SSL_CERT_FILE, so the CA is read from a tmpfs that BuildKit
# discards with the RUN and the variable is shell-scoped rather than an ENV. The published image
# carries the stock trust store and no trace of the secret. Do NOT "simplify" this by appending to
# /etc/ssl/certs/ca-certificates.crt — that would bake a session-local CA into a released image.
RUN --mount=type=secret,id=proxy_ca \
    --mount=type=tmpfs,target=/tmp/ca \
    if [ -s /run/secrets/proxy_ca ]; then \
        cat /etc/ssl/certs/ca-certificates.crt /run/secrets/proxy_ca > /tmp/ca/bundle.crt && \
        export SSL_CERT_FILE=/tmp/ca/bundle.crt; \
    fi; \
    apk add --no-cache icu-libs icu-data-full
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

# The Data Protection key ring lives here (issue #444). Created at BUILD TIME with the right owner
# because a named volume mounted at a path absent from the image is created root:root 0755 by the
# daemon, and `app` would then be unable to write it — which Data Protection meets by silently falling
# back to an in-memory ring. Docker copies this ownership into the volume only when the volume is
# EMPTY, so an existing deployment needs the one-line chown in docs/deployment.md; the API asserts
# writability at startup rather than continuing silently.
RUN mkdir -p /var/odyssey/dataprotection-keys && chown -R app:app /var/odyssey/dataprotection-keys

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# Run unprivileged: the aspnet base image ships a non-root "app" user (UID 1654). Set it
# explicitly so an app-level compromise can't act as root inside the container, and so the
# posture doesn't silently regress if the base image default changes. Port 8080 is >1024, so
# no privileged bind is needed.
USER app

ENTRYPOINT ["dotnet", "Odyssey.Api.dll"]
