FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine@sha256:620e765fe18186c08399f7aa978f79f04b6bbf0ee1b3b8a91e2d5c9619e59da1 AS build

COPY . /src
WORKDIR /src
RUN dotnet restore
RUN dotnet publish Odyssey.MigrationService/Odyssey.MigrationService.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine@sha256:c4b29bf368004ad9076c1ab9bc91fb373561e3905b4345637e14e8b8c57e3be8 AS final
WORKDIR /app
COPY --from=build /app/publish .

# No Data Protection key directory here (issue #444 §14, revisited). This image briefly created one,
# because the job mounted the API's key ring so a config-adoption step could protect a secret under
# keys the API could read. That step is gone, the job protects nothing, and every extra holder of the
# ring is a container that can decrypt every stored credential. The API's Dockerfile keeps its copy.

# Run unprivileged: the aspnet base image ships a non-root "app" user (UID 1654). Set it
# explicitly so the migration runner doesn't execute as root inside the container.
USER app

ENTRYPOINT ["dotnet", "Odyssey.MigrationService.dll"]
