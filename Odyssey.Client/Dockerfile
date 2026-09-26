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
RUN dotnet publish Odyssey.Client/Odyssey.Client.csproj -c Release -o /app/publish

# Unprivileged nginx: runs as the non-root "nginx" user (UID 101) and listens on 8080 instead of
# 80, so no part of the web tier runs as root. The site config (nginx.conf) listens on 8080 to
# match; Caddy and the compose port mappings target 8080.
FROM nginxinc/nginx-unprivileged:1.31-alpine@sha256:6a23acdfca2b9cfbcec61419e3f1426bcbedb91362f2f19306a8567423bb4612 AS final
COPY Odyssey.Client/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/publish/wwwroot /usr/share/nginx/html
EXPOSE 8080
