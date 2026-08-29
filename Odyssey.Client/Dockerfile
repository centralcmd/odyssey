FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine@sha256:620e765fe18186c08399f7aa978f79f04b6bbf0ee1b3b8a91e2d5c9619e59da1 AS build

COPY . /src
WORKDIR /src
RUN dotnet restore
RUN dotnet publish Odyssey.Client/Odyssey.Client.csproj -c Release -o /app/publish

# Unprivileged nginx: runs as the non-root "nginx" user (UID 101) and listens on 8080 instead of
# 80, so no part of the web tier runs as root. The site config (nginx.conf) listens on 8080 to
# match; Caddy and the compose port mappings target 8080.
FROM nginxinc/nginx-unprivileged:1.31-alpine@sha256:901e944d1f4fc2bd077e8f5568b98c1f6f8cdacf6b97a87747c43134a339b9a7 AS final
COPY Odyssey.Client/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/publish/wwwroot /usr/share/nginx/html
EXPOSE 8080
