# Matcad

A web UI for managing [Caddy](https://caddyserver.com/) including DNS provider modules
(e.g. Netcup), hierarchical routes (wildcard / fallback), centrally managed
authentications, log statistics and a local user/role system.

## Stack

- **matcad** — ASP.NET Core (.NET 10, Razor Pages), UI on port **4433**
- **caddy** — custom image (xcaddy) with DNS modules; config is pushed at runtime via the admin API
- **SQLite** — logic (users, sessions, request logs) on the data volume
- **JSON** — configs (providers, routes, authentications, settings) on the data volume

## Deploy (homelab / production)

Pull the prebuilt images from GHCR and start the stack — no local build:

```bash
docker compose pull
docker compose up -d
```

- `docker-compose.yml` uses `ghcr.io/real-ttx/matcad` + `ghcr.io/real-ttx/matcad-caddy`.
- Pin a version instead of `latest`: `MATCAD_VERSION=0.2.0 docker compose up -d`.
- UI: <http://localhost:4433> (put it behind Caddy or your VPN).
- Default login on first start: **admin / admin**. A fresh install starts empty
  and runs a **setup wizard** (account password → domains/login → optional DNS
  provider → finish). Re-run it any time under *Settings → General*. Demo data
  can be loaded on demand under *Settings → Import / Export*.

The published images bake in these Caddy DNS-provider modules: netcup, cloudflare,
digitalocean, hetzner, route53, gandi, desec, ovh. To use others, build locally
(see below) with an extended `CADDY_DNS_MODULES`.

## Compose files

| File | Role |
|------|------|
| `docker-compose.yml` | **Deploy** — pulls release images from GHCR (homelab / production). |
| `docker-compose.dev.yml` | **Develop** — builds locally, adds a whoami test upstream, exposes Caddy admin. |
| `docker-compose.release.yml` | Build the release images locally (CI normally publishes them to GHCR). |

## Development / testing

Live reload = rebuild the container and redeploy the stack:

```powershell
./scripts/deploy.ps1            # dev  (whoami test upstream, Caddy on 8080/8443, admin 2019)
./scripts/deploy.ps1 -Mode release
```

- UI: <http://localhost:4433>
- Caddy admin (dev only): <http://localhost:2019/config/>

## Releases

Push a version tag and GitHub Actions builds + publishes both images to GHCR:

```bash
git tag v0.2.0 && git push origin v0.2.0
```

## Authentication types

- **Basic Auth** — managed user list (bcrypt) → Caddy `basic_auth`.
- **Matcad** — login portal with redirect (Caddy `forward_auth`). Unauthenticated
  requests are redirected to the Matcad portal and, after signing in, sent back
  to the endpoint. Requirements (under *Settings*):
  - **Base domain** (e.g. `example.com`) — the portal cookie is shared across
    subdomains.
  - **Login portal URL** — an *unprotected* host that points to Matcad
    (e.g. a route `auth.example.com` → `http://matcad:4433`).

## Docker discovery mode

Optionally binds running containers as routes (Settings → Docker). Default name
`<containername>.<base-domain>`; fine-tune via labels `matcad.enable=true`,
`matcad.host`, `matcad.port`, `matcad.auth`. Requires the Docker socket mounted
read-only into the Matcad container (already configured in the compose files).

## Real-time logs

Caddy writes JSON access logs to a shared volume; Matcad ingests them (SQLite)
and streams new entries to the dashboard via Server-Sent Events.

## Versioning

`scripts/version.ps1` computes the version and increments the build counter in `version.json`:

- Release: `<major>.<minor>.<build>-<yyyyMMdd>`
- Nightly: `nightly-<build>-<yyyyMMdd>`
- Local: `local-<yyyyMMdd>`

## Branches

- `main` — release
- `dev` — development
