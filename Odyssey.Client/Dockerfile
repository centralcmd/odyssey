FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine@sha256:4ac537e13e2f55d1d588ed3e618cb0cb6b82dd8deb17830de43d5086fbde958b AS build

COPY . /src
WORKDIR /src
RUN dotnet restore
RUN dotnet publish Odyssey.Client/Odyssey.Client.csproj -c Release -o /app/publish

# Unprivileged nginx: runs as the non-root "nginx" user (UID 101) and listens on 8080 instead of
# 80, so no part of the web tier runs as root. The site config (nginx.conf) listens on 8080 to
# match; Caddy and the compose port mappings target 8080.
FROM nginxinc/nginx-unprivileged:1.31-alpine@sha256:2ddec616f1cb58bcac057aa388f28cb81e35137641ef4226d321714499329bd1 AS final
COPY Odyssey.Client/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/publish/wwwroot /usr/share/nginx/html
EXPOSE 8080
