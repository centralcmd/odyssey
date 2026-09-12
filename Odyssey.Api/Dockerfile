FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine@sha256:4ac537e13e2f55d1d588ed3e618cb0cb6b82dd8deb17830de43d5086fbde958b AS build

COPY . /src
WORKDIR /src
RUN dotnet restore
RUN dotnet publish Odyssey.Api/Odyssey.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine@sha256:6bb0fab0ef31f44f710a668c39c2263ae810f5adf868afa34cbd86815912c7fe AS final
WORKDIR /app
COPY --from=build /app/publish .

# Globalization (issue #48). The alpine runtime image ships no ICU, so .NET falls back to INVARIANT
# globalization — under which CompareOptions.IgnoreNonSpace silently stops folding accents. That is
# the comparison the contact-alias duplicate check uses to agree with the column's utf8mb4_*_ci unique
# index, so without ICU the service would accept "Renee" against a stored "Renée" and let the INDEX
# reject it: a 500-shaped failure on an ordinary write, reproducible nowhere else — a dev box and both
# fast test tiers have ICU. Installed here, asserted at startup by GlobalizationGuard, and the
# environment variable is pinned false so a base-image change cannot silently re-enable invariant mode.
RUN apk add --no-cache icu-libs icu-data-full
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
