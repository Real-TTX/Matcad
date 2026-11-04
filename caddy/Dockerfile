# syntax=docker/dockerfile:1

# Build a custom Caddy with DNS provider modules. The module list is a single
# source of truth in .env (CADDY_DNS_MODULES); add providers there and rebuild.
#
# The builder runs on the native BUILD platform and cross-compiles for the target
# architecture (Go cross-compiles via GOOS/GOARCH), so multi-arch builds
# (linux/amd64 + linux/arm64) stay fast with no QEMU emulation.
FROM --platform=$BUILDPLATFORM caddy:2-builder AS builder
ARG CADDY_DNS_MODULES="github.com/caddy-dns/netcup"
# Optional go-module replaces ("old=new@ref", space-separated); see .env CADDY_DNS_REPLACES.
# Used to point caddy-dns/netcup at a libdns fork with the new CloudDNS REST API.
ARG CADDY_DNS_REPLACES=""
ARG TARGETOS
ARG TARGETARCH
RUN set -eu; export GOOS="${TARGETOS:-linux}" GOARCH="${TARGETARCH:-amd64}"; \
    args=""; for m in $CADDY_DNS_MODULES; do args="$args --with $m"; done; \
    for r in $CADDY_DNS_REPLACES; do args="$args --replace $r"; done; \
    eval "xcaddy build $args"

FROM caddy:2
COPY --from=builder /usr/bin/caddy /usr/bin/caddy
# Bootstrap config: enable the admin API so Matcad can push config at runtime.
COPY Caddyfile /etc/caddy/Caddyfile
