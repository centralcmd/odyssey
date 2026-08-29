FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine@sha256:620e765fe18186c08399f7aa978f79f04b6bbf0ee1b3b8a91e2d5c9619e59da1 AS build

COPY . /src
WORKDIR /src
RUN dotnet restore
RUN dotnet publish Odyssey.MigrationService/Odyssey.MigrationService.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine@sha256:c4b29bf368004ad9076c1ab9bc91fb373561e3905b4345637e14e8b8c57e3be8 AS final
WORKDIR /app
COPY --from=build /app/publish .

# The Data Protection key ring lives here (issue #444). Created at BUILD TIME with the right owner
# because a named volume mounted at a path absent from the image is created root:root 0755 by the
# daemon, and `app` would then be unable to write it — which Data Protection meets by silently falling
# back to an in-memory ring. Docker copies this ownership into the volume only when the volume is
# EMPTY, so an existing deployment needs the one-line chown in docs/deployment.md; the API asserts
# writability at startup rather than continuing silently.
RUN mkdir -p /var/odyssey/dataprotection-keys && chown -R app:app /var/odyssey/dataprotection-keys

# Run unprivileged: the aspnet base image ships a non-root "app" user (UID 1654). Set it
# explicitly so the migration runner doesn't execute as root inside the container.
USER app

ENTRYPOINT ["dotnet", "Odyssey.MigrationService.dll"]
