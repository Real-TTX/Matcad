# CLAUDE.md

Guidance for working in this repository. Keep it in sync with the code.

## What Matcad is

A web UI for the Caddy reverse proxy: routes, wildcard HTTPS through DNS providers, a login portal (forward auth), statistics,
Docker discovery, Caddyfile import. ASP.NET Core on **.NET 10**, **Razor Pages**, EF Core **SQLite** for users, sessions and the
request log, **JSON files** for configuration, plain JavaScript (no build step). Port **4433**. Matcad pushes the complete Caddy
JSON config to Caddy's admin API after every change.

Siblings that depend on it: **Mattiverse** installs Matcad (its catalog shows `admin` / `admin` and generates `Matcad__ApiKey`)
and **MatOS** calls the REST API (`/api/v1`, header `X-Api-Key`). Keep those contracts: seeded `admin`/`admin` that must change
its password, the API routes and their JSON shapes, idempotent `POST /api/v1/routes` (same host or port = same route).

## Build / run / test

- **Build:** `dotnet build src/Matcad/Matcad.csproj` (expected: 0 warnings). **Tests:** `dotnet test tests/Matcad.Tests`
  (unit tests plus the whole app in memory via `WebApplicationFactory`).
- **Run locally:** from `src/Matcad`, `Matcad__DataDir=<dir> Matcad__ConfigDir=<dir>/config Matcad__Port=4466 dotnet bin/Release/net10.0/Matcad.dll`.
  Port 4433 is often taken by a Docker stack on the dev PC. Razor views are compiled at build time: rebuild and restart after a
  `.cshtml` change (CSS and JS are read from disk).
- **End to end:** the Docker stack (Caddy + Matcad) driven by Playwright with the installed Edge (see the `screenshots` skill).
  A very long path breaks the native SQLite library on Windows (`e_sqlite3`): run scratch projects from a short path.

## Map

```
src/Matcad
  Program.cs            pipeline, auth, health check, SSE log stream
  Api/                  REST API for matOS / Mattiverse (X-Api-Key)
  Auth/                 session cookie scheme, account guard (forced password change, first-run wizard)
  Config/               JSON config (ConfigStore: immutable snapshots), RouteEditor (shared route rules), certificates, backup
  Data/                 EF Core context, migrations, DatabaseBootstrapper (also upgrades pre-0.6 databases)
  Services/             Caddy config generator + admin client, login throttle, forward-auth tokens, Docker discovery, log ingest
  Pages/                Razor Pages; Shared/ has the layouts (_Layout app, _PlainLayout sign-in/setup, _PortalLayout login portal)
  Controls/             tag helpers (<icon>, <mat-field>, <mat-form-buttons>) and the data-table model
  wwwroot/              css/matcad.css, js/matcad.js, sw.js, offline.html, manifest.webmanifest, img/
tests/Matcad.Tests
```

## Conventions

**Code:** Microsoft C# guidelines, file-scoped namespaces, short comments that explain *why*. Pages work on a copy of the stored
object and save only after validation (a rejected form must not change stored state).

**Security:** fail closed (a route that names a missing authentication or a basic auth without users answers 503, never open;
a Docker container with an unknown `matcad.auth` label is not published). Sign-in is throttled per account and client
(`LoginAttemptTracker`). The forward-auth cookie carries a fingerprint of the password hash, so a new password ends old sessions.

**Database:** SQLite for logic, JSON for config. Tables PascalCase and singular, key `Id` (64 bit), audit columns, index and
constraint names PascalCase without underscores. Schema changes are migrations:
`dotnet ef migrations add <Name> --project src/Matcad --output-dir Data/Migrations` (tool in `dotnet-tools.json`).

**UI:** the design system of Mattiverse (iOS/Material look, `mat-*` classes, theme switch bottom left, hidden scrollbars until
scrolling, pill buttons in one line: Save, Back, space, Delete). Phone width (390 px) must work: secondary table columns get
`hide-sm`. The top bar and the area behind the status bar are always dark (`--chrome`), because an installed iOS app has white
status bar icons. Inputs are 16 px on touch screens (no zoom on focus); do not disable pinch zoom.

**PWA:** `sw.js` caches static files only and serves `offline.html`; it never stores pages or queues requests (signed-in views of a
live system). It is registered as `/sw.js?v=<app version>`. `/healthz` is the reachability probe for the banner, the offline page
and the Docker health check. `matcad.js` shows the connection banner, keeps typed input and reloads clean pages after a reconnect.

**Git:** commit under the user's name only, no Claude/Anthropic trailer. Branches: `main` release, `dev` development.
