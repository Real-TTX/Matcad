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
  Pages/                Razor Pages; Shared/ has the layouts (_Layout app, _PlainLayout sign-in/setup, _PortalLayout login portal) and _Toolbar
  Controls/             Ui (icons, logo), tag helpers (<icon>, <mat-field>, <mat-form-buttons>), ToolbarModel, BrandAccent
  wwwroot/              matui/ (the toolkit, a copy), css/matcad.css, js/*.js, sw.js, offline.html, manifest.webmanifest, img/
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

**UI:** the design system of the Mat apps, **MatUi** (the toolkit of the MatDesign project; rules and live examples are on its site,
the rule ids such as SHELL-08 are in `MatDesign/src/MatDesign.Core/RuleBook.cs`). `wwwroot/matui/` is a *copy* (`matui.css`, `matui.js`,
`matui.boot.js`, `matui-icons.svg`); `scripts/update-matui.ps1` fetches a newer one from a running MatDesign, then look at every page.
The only colour Matcad chooses is the accent: `data-app="matcad"` on `<html>` (`#2f6feb`, set in `matui.css`, kept in `Ui.App.Accent`).
Pages use only toolkit classes (`mat-*`): flat surfaces, a breadcrumb above every page (`ViewData["Crumbs"] = Crumbs.Of(...)`), a title
and one line (`PageSub`), lists as toolbar / table / actions below (`_Toolbar` partial, `ToolbarModel`), forms as cards with the button
row under them (Save, Back, space, Delete), confirmations with `data-confirm`, dependent fields with `data-show-if`, every `td` with
`data-label` (rows become cards on a phone), icons from the sprite (`@Ui.Icon("name")` or `<icon name="..." />`; a test checks the names).
No inline styles, scripts or event handlers and no colour literals: what is Matcad's own lives in `css/matcad.css` with the prefix `mc-`
and in small page scripts (`route-edit.js`, `authentication-edit.js`). Phone width (390 px) must work; inputs are 16 px on touch
screens (no zoom on focus); do not disable pinch zoom. The login portal is served on the protected host, where Caddy proxies only
`/auth/*`: `_PortalLayout` loads the toolkit from `/auth/ui/` and `matcad.css` from `/auth/css/`, and takes its colour from the
authentication (`data-accent="custom"`).

**PWA:** `sw.js` caches static files only (the toolkit included) and serves `offline.html`, which uses the toolkit too; it never
stores pages or queues requests (signed-in views of a live system). It is registered as `/sw.js?v=<app version>`. `/healthz` is the
reachability probe for the banner, the offline page and the Docker health check. `matcad.js` shows the connection banner, keeps typed
input, reloads clean pages after a reconnect and keeps the colour of the browser bar (`theme-color`) in step with the theme.

**Git:** commit under the user's name only, no Claude/Anthropic trailer. Branches: `main` release, `dev` development.
