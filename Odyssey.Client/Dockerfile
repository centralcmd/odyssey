FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine@sha256:620e765fe18186c08399f7aa978f79f04b6bbf0ee1b3b8a91e2d5c9619e59da1 AS build

COPY . /src
WORKDIR /src
RUN dotnet restore
RUN dotnet publish Odyssey.Client/Odyssey.Client.csproj -c Release -o /app/publish

# Unprivileged nginx: runs as the non-root "nginx" user (UID 101) and listens on 8080 instead of
# 80, so no part of the web tier runs as root. The site config (nginx.conf) listens on 8080 to
# match; Caddy and the compose port mappings target 8080.
FROM nginxinc/nginx-unprivileged:1.31-alpine@sha256:aa8c9087d36d93e9d650c5365f883b421e8214aedbad24ade52b844c583358f1 AS final
COPY Odyssey.Client/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/publish/wwwroot /usr/share/nginx/html
EXPOSE 8080
