# syntax=docker/dockerfile:1

# ---- Caddy binary (for `caddy adapt` during Caddyfile import) ----
# Same DNS modules as the running Caddy (single source: .env CADDY_DNS_MODULES),
# otherwise adapting a config that uses e.g. `dns netcup` fails.
FROM caddy:2-builder AS caddybuild
ARG CADDY_DNS_MODULES="github.com/caddy-dns/netcup"
RUN set -eu; args=""; for m in $CADDY_DNS_MODULES; do args="$args --with $m"; done; \
    eval "xcaddy build $args"

# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG VERSION=0.0.0-local
WORKDIR /src
COPY src/Matcad/Matcad.csproj src/Matcad/
RUN dotnet restore src/Matcad/Matcad.csproj
COPY src/ src/
RUN dotnet publish src/Matcad/Matcad.csproj -c Release -o /app \
    -p:InformationalVersion=${VERSION}

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
# Static Caddy binary (with the same DNS modules), for `caddy adapt` import only.
COPY --from=caddybuild /usr/bin/caddy /usr/local/bin/caddy
# Data volume holds the SQLite database + JSON configs.
VOLUME ["/data"]
EXPOSE 4433
ENV ASPNETCORE_ENVIRONMENT=Production
ENTRYPOINT ["dotnet", "Matcad.dll"]
