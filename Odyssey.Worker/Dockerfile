# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine@sha256:230c3fbae0ceedc70bca04eed2bfb5694e8b248cadbb81010f41b7a993601f55 AS build

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
RUN dotnet publish Odyssey.Worker/Odyssey.Worker.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine@sha256:819bd3649d8bf53f35527c68d62e17b348ef6f24ae73073f497f00a878348062 AS final
WORKDIR /app
COPY --from=build /app/publish .

# tzdata (issues #348, #349): the weather job composes each location's days in its own IANA zone, the
# exchange-rate sync job dates Norges Bank's rates in Europe/Oslo, and the alpine
# runtime image ships no time-zone database. Asserted at startup by TimeZoneDataGuard, as in the API. No
# ICU: unlike the API (issue #48), nothing this host runs compares or folds text, and TimeZoneInfo reads
# the zone files directly. The optional CA mount is the API's, for the same apk-over-TLS reason; nothing
# from it persists in this shipped layer.
RUN --mount=type=secret,id=proxy_ca \
    --mount=type=tmpfs,target=/tmp/ca \
    if [ -s /run/secrets/proxy_ca ]; then \
        cat /etc/ssl/certs/ca-certificates.crt /run/secrets/proxy_ca > /tmp/ca/bundle.crt && \
        export SSL_CERT_FILE=/tmp/ca/bundle.crt; \
    fi; \
    apk add --no-cache tzdata

# The worker only READS the API's Data Protection key ring, from the API's keys volume mounted read-only
# at DataProtection__KeysPath (see docker-compose.yml). Holding that ring lets this container decrypt
# every stored credential — the deliberate trade-off recorded in docs/deployment.md. It never writes a
# key: automatic key generation is off in WorkerHost. The directory is still created here, owned by
# `app`, for the API Dockerfile's reason: if this container is the first to mount a fresh volume, Docker
# initialises the volume from this path, and a root-owned one would leave the API unable to write keys.
RUN mkdir -p /var/odyssey/dataprotection-keys && chown -R app:app /var/odyssey/dataprotection-keys

# Run unprivileged: the aspnet base image ships a non-root "app" user (UID 1654). Set it
# explicitly so the worker doesn't run as root inside the container. The API's keys are written by the
# same UID, so the read-only mount is readable without any permission change.
USER app

ENTRYPOINT ["dotnet", "Odyssey.Worker.dll"]
