# syntax=docker/dockerfile:1

# ---- Caddy binary (for `caddy adapt` during Caddyfile import) ----
# Must carry the SAME DNS modules as caddy/Dockerfile, otherwise adapting a
# config that uses e.g. `dns netcup` fails. Keep the --with list in sync.
FROM caddy:2-builder AS caddybuild
RUN xcaddy build \
    --with github.com/caddy-dns/netcup

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
