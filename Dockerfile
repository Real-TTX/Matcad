# syntax=docker/dockerfile:1

# Multi-arch note: the caddybuild and .NET build stages run on the native BUILD
# platform ($BUILDPLATFORM) and cross-compile for the target architecture
# ($TARGETARCH). So building linux/amd64 + linux/arm64 needs no QEMU emulation
# and stays fast. Only COPY/ENV run in the target-arch runtime stage.

# ---- Caddy binary (for `caddy adapt` during Caddyfile import) ----
# Same DNS modules as the running Caddy (single source: .env CADDY_DNS_MODULES),
# otherwise adapting a config that uses e.g. `dns netcup` fails.
FROM --platform=$BUILDPLATFORM caddy:2-builder AS caddybuild
ARG CADDY_DNS_MODULES="github.com/caddy-dns/netcup"
ARG TARGETOS
ARG TARGETARCH
RUN set -eu; export GOOS="${TARGETOS:-linux}" GOARCH="${TARGETARCH:-amd64}"; \
    args=""; for m in $CADDY_DNS_MODULES; do args="$args --with $m"; done; \
    eval "xcaddy build $args"

# ---- Build stage (cross-publishes for $TARGETARCH) ----
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG VERSION=0.0.0-local
ARG TARGETARCH
WORKDIR /src
COPY src/Matcad/Matcad.csproj src/Matcad/
# Map Docker's TARGETARCH (amd64/arm64) to the .NET arch id (x64/arm64).
RUN a=x64; [ "$TARGETARCH" = "arm64" ] && a=arm64; \
    dotnet restore src/Matcad/Matcad.csproj -a $a
COPY src/ src/
RUN a=x64; [ "$TARGETARCH" = "arm64" ] && a=arm64; \
    dotnet publish src/Matcad/Matcad.csproj -c Release -o /app -a $a \
      --no-restore -p:InformationalVersion=${VERSION}

# ---- Runtime stage (target architecture) ----
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
