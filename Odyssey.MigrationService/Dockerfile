FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine@sha256:4ac537e13e2f55d1d588ed3e618cb0cb6b82dd8deb17830de43d5086fbde958b AS build

COPY . /src
WORKDIR /src
RUN dotnet restore
RUN dotnet publish Odyssey.MigrationService/Odyssey.MigrationService.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine@sha256:6bb0fab0ef31f44f710a668c39c2263ae810f5adf868afa34cbd86815912c7fe AS final
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
