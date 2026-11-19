# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine@sha256:0294f285d062afacc8b5f6d7febb0cd4ee879320e5585fa15f2f4ebf1feb9ba7 AS build

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

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine@sha256:21c3f9798416cba4c0576a947c286231cb71ea2884896cb968253f5c9668f930 AS final
WORKDIR /app
COPY --from=build /app/publish .

# tzdata (issues #348, #349): the weather job composes each location's days in its own IANA zone, the
# exchange-rate sync job dates Norges Bank's rates in Europe/Oslo, and the alpine
# runtime image ships no time-zone database. Asserted at startup by TimeZoneDataGuard, as in the API.
# ICU (issue #470 §8.2): the news reader's URL keys map IDN hosts to punycode and normalise text through ICU,
# and the worker must key a feed exactly as the API does. Asserted at startup by GlobalizationGuard, and the
# environment variable is pinned false so a base-image change cannot silently re-enable invariant mode.
# The optional CA mount is the API's, for the same apk-over-TLS reason; nothing from it persists in this
# shipped layer.
RUN --mount=type=secret,id=proxy_ca \
    --mount=type=tmpfs,target=/tmp/ca \
    if [ -s /run/secrets/proxy_ca ]; then \
        cat /etc/ssl/certs/ca-certificates.crt /run/secrets/proxy_ca > /tmp/ca/bundle.crt && \
        export SSL_CERT_FILE=/tmp/ca/bundle.crt; \
    fi; \
    apk add --no-cache icu-libs icu-data-full tzdata
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

# The worker only READS the API's Data Protection key ring, from the API's keys volume mounted read-only
# at DataProtection__KeysPath (see docker-compose.yml). Holding that ring lets this container decrypt
# every stored credential — the deliberate trade-off recorded in docs/deployment.md. It never writes a
# key: automatic key generation is off in WorkerHost. The directory is still created here, owned by
# `app`, for the API Dockerfile's reason: if this container is the first to mount a fresh volume, Docker
# initialises the volume from this path, and a root-owned one would leave the API unable to write keys.
RUN mkdir -p /var/odyssey/dataprotection-keys && chown -R app:app /var/odyssey/dataprotection-keys

# The file volumes' mount point (issue #346 A3.2, #362 Amendment v7.1 §7). The worker mounts every file volume
# read-write, and if it is the first container to mount a fresh one, Docker initialises the volume from this path —
# so it is owned by `app` here too, or the API could not write the volume it is the one to initialise.
RUN mkdir -p /var/odyssey/files/primary && chown -R app:app /var/odyssey/files && chmod -R 0750 /var/odyssey/files

# Run unprivileged: the aspnet base image ships a non-root "app" user (UID 1654). Set it
# explicitly so the worker doesn't run as root inside the container. The API's keys are written by the
# same UID, so the read-only mount is readable without any permission change.
USER app

ENTRYPOINT ["dotnet", "Odyssey.Worker.dll"]
