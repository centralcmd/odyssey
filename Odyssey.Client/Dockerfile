# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine@sha256:cc02b814237a76c4735fb6eeee87181597b5e56f2ce458a435e527674d91f4d7 AS build

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
# 80, so no part of the web tier runs as root. The site config listens on 8080 to match; Caddy and
# the compose port mappings target 8080.
FROM nginxinc/nginx-unprivileged:1.31-alpine@sha256:b5047b9b76845eac124358373c6852377c52e4b05834a47647e955dd91cb8f1e AS final

# The site config is a TEMPLATE (issue #370): the image's stock 20-envsubst-on-templates.sh renders
# nginx/templates/default.conf.template into /etc/nginx/conf.d/default.conf at start, so the CSP can
# carry the deploy-time map tile origins. The filter limits substitution to that ONE variable, so
# nginx's own $host, $scheme and $forwarded_proto pass through untouched.
#
# The base image's welcome default.conf is deleted here so no fallback config can ever be served: if
# the render fails (a read-only conf.d), 25-assert-rendered-config.sh stops the container instead.
# conf.d is owned by the nginx user in this image, so the rm needs no USER root.
RUN rm -f /etc/nginx/conf.d/default.conf
COPY Odyssey.Client/nginx/templates/ /etc/nginx/templates/
# 15- runs before the stock 20-envsubst step and validates the value; 25- runs after it and asserts the
# rendered result. --chmod makes the executable bit independent of the checkout.
COPY --chmod=755 Odyssey.Client/nginx/docker-entrypoint.d/ /docker-entrypoint.d/
ENV NGINX_ENVSUBST_FILTER=^MAP_ALLOWED_ORIGINS\$
# The shipped default. Pinned equal to Odyssey.Api/appsettings.json, docker-compose.yml and the three
# .env*.example files by MapDefaultsAgreementTests. Empty is the off-switch; see docs/deployment.md.
ENV MAP_ALLOWED_ORIGINS=https://tiles.openfreemap.org
COPY --from=build /app/publish/wwwroot /usr/share/nginx/html
EXPOSE 8080
