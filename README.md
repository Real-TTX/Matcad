# Matcad

Eine Web-UI zur Verwaltung von [Caddy](https://caddyserver.com/) inkl. DNS-Provider-Modulen
(z. B. Netcup), hierarchischen Routen (Wildcard / Fallback), zentral verwalteten
Authentications, Log-Statistiken und lokalem Benutzer-/Rollensystem.

## Stack

- **matcad** — ASP.NET Core (.NET 10, Razor Pages), UI auf Port **4433**
- **caddy** — eigenes Image (xcaddy) mit DNS-Modulen; Config wird zur Laufzeit per Admin-API gepusht
- **SQLite** — Logik (Users, Sessions, Request-Logs) auf dem Daten-Volume
- **JSON** — Configs (Providers, Routes, Authentications, Settings) auf dem Daten-Volume

## Entwicklung / Testen

Live-Reload = Container neu bauen und Stack redeployen:

```powershell
./scripts/deploy.ps1            # dev  (whoami-Test-Upstream, Caddy auf 8080/8443, Admin 2019)
./scripts/deploy.ps1 -Mode release
```

- UI: <http://localhost:4433>
- Caddy Admin (nur dev): <http://localhost:2019/config/>
- Standard-Login beim ersten Start: **admin / admin** — bitte sofort unter
  *Settings → Benutzer* ändern.

## Authentication-Typen

- **Basic Auth** — verwaltete Benutzerliste (bcrypt) → Caddy `basic_auth`.
- **Matcad** — Login-Portal mit Weiterleitung (Caddy `forward_auth`). Nicht
  angemeldete Zugriffe werden zum Matcad-Portal umgeleitet, nach der Anmeldung
  zurück zum Endpunkt. Voraussetzung (unter *Settings*):
  - **Basis-Domain** (z. B. `example.com`) — der Portal-Cookie gilt
    subdomain-übergreifend.
  - **Login-Portal-URL** — ein *ungeschützter* Host, der auf Matcad zeigt
    (z. B. eine Route `auth.example.com` → `http://matcad:4433`).

## Echtzeit-Logs

Caddy schreibt JSON-Access-Logs auf ein gemeinsames Volume; Matcad liest sie
ein (SQLite) und streamt neue Einträge per Server-Sent Events ins Dashboard.

## Versionierung

`scripts/version.ps1` erzeugt die Version und zählt die Buildnummer in `version.json` hoch:

- Release: `<major>.<minor>.<build>-<yyyyMMdd>`
- Nightly: `nightly-<build>-<yyyyMMdd>`
- Local: `local-<yyyyMMdd>`

## Branches

- `main` — Release
- `dev` — Development
