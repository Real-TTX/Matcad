# syntax=docker/dockerfile:1

# Build a custom Caddy with DNS provider modules (extend the --with list to add more).
FROM caddy:2-builder AS builder
RUN xcaddy build \
    --with github.com/caddy-dns/netcup

FROM caddy:2
COPY --from=builder /usr/bin/caddy /usr/bin/caddy
# Bootstrap config: enable the admin API so Matcad can push config at runtime.
COPY Caddyfile /etc/caddy/Caddyfile
