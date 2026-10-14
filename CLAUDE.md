# CLAUDE.md

Guidance for working in this repository. Keep it in sync with the code.

## What Matcad is

A web UI for the Caddy reverse proxy: routes, wildcard HTTPS through DNS providers, a login portal (forward auth), statistics,
Docker discovery, Caddyfile import. ASP.NET Core on **.NET 10**, **Razor Pages**, EF Core **SQLite** for users, sessions and the
request log, **JSON files** for configuration, plain JavaScript (no build step). Port **4433**. Matcad pushes the complete Caddy
JSON config to Caddy's admin API after every change.

Siblings that depend on it: **Mattiverse** installs Matcad (its catalog shows `admin` / `admin` and generates `Matcad__ApiKey`)
and **MatOS** calls the REST API (`/api/v1`, header `X-Api-Key`). Keep those contracts: seeded `admin`/`admin` that must change
its password, the API routes and their JSON shapes, idempotent `POST /api/v1/routes` (same host or port = same route). New fields are
only ever ADDED. The **MatCMS Cloud** (another repo) chains two proxies through the route access properties below and reads
`GET /api/v1/status` -> `capabilities` (`["routeGuards"]`) to know that this Matcad enforces them.

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
  Api/                  REST API for matOS / Mattiverse / the Cloud (X-Api-Key); RouteAccessFields reads the tri-state access fields
  Auth/                 session cookie scheme, account guard (forced password change, first-run wizard)
  Config/               JSON config (ConfigStore: immutable snapshots), RouteEditor (shared route rules), RouteGuards (rules for the access properties), certificates, backup
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

**Route access properties** (all optional, off by default; `RouteConfig`, rules in `Config/RouteGuards.cs`, applied by `RouteEditor.Apply` for the
page and the API alike, built in `CaddyConfigGenerator`): `UpstreamHost` (Host header to the upstream; for an `https://` upstream also
`transport.tls.server_name`), `UpstreamHeaders` (set on the upstream request), `RequireHeader` (guard: no exact header + value = 403) and
`AllowedIps` (guard: direct TCP peer not in the list = 403; IPs or CIDR, IPv4/IPv6). Things to keep true:
- The guard is the **first handler** of `BuildHandlers` (before auth, framing strip and proxy), a `subroute` of `not`-matcher routes with
  `static_response` 403 and no body (one refusal per guard, so header missing OR address wrong refuses). `BuildHandlers` is shared by host
  routes, port-bound routes and Docker routes, so the guard holds wherever the chain is used; the inline login route (`/auth/*`) of a guarded
  host carries the guard too. It also wins over the 503 of a missing authentication.
- **Fail closed:** anything configured but invalid (a restored backup or hand-edited `routes.json` can carry what the editor refuses, an IP list
  that normalises to nothing) answers 503 for the whole route, never serves it open (`BuildGuard` returns null, `RouteGuards.Check`).
- Caddy traps behind the validation: the header matcher treats `*` as a wildcard and replaces `{...}` placeholders in the value, so a required
  value with `*`, `{` or `}` is refused, and it needs 16+ visible ASCII characters (0x21-0x7E: no space, tab, NUL, umlaut - HTTP trims the
  edges of a received value, so a secret with an edge space could never match and would lock every caller out); upstream header values refuse
  braces too (Caddy would swallow them), control characters and a space at either edge. Names are HTTP tokens that start with a letter/digit;
  `Host`, `Content-Length`, `Transfer-Encoding`, `Connection` are refused (Host has its own field). Caddy sets a header under exactly the key it
  is given, so names are written canonical (`RouteGuards.CanonicalName`), otherwise `x-foo` would sit next to the visitor's `X-Foo`.
- The required header is **removed from the request before it goes upstream** (`headers.request.delete`, merged into the same `headers` object as
  the Host and the `set` entries): the website behind, its plugins and its logs must never see the secret. Caddy applies `delete` AFTER `set`, so an
  upstream header with the same name as the required header would vanish again: that clash is refused (`RouteGuards.CheckNameClash`: 400 from
  the editor and the API, 503 for a stored route that has it).
- `remote_ip` is the TCP peer, not `X-Forwarded-For`; behind Docker Desktop / userland proxy / NAT that can be the gateway. A mapped IPv4
  (`::ffff:1.2.3.4`) is stored as IPv4, a range with host bits is stored masked (`10.1.2.3/8` -> `10.0.0.0/8`).
- **Secrets never come out:** REST responses show `upstreamHost` and `allowedIps` as they are, but `upstreamHeaders` as
  `[{name, valueSet}]` and `requireHeader` as `{name, valueSet}` (also in `GET /routes`, `/routes/manual`, `/routes/{id}` and the POST answer);
  the editor sends no value back (password inputs, "Set - leave empty to keep"; a blank value keeps the saved one by header name, an empty
  required-header name removes the guard); error messages name field and reason, never a value; `SecretRedaction` replaces them in the config
  shown on Settings -> Caddy; the access-log encoder (`filter`, `delete` on `request>headers>Name`) cuts the required header out of Caddy's
  `access.log` (Matcad's own ingest stores only host/path/method/status/ip/duration). A backup file does contain them (like provider
  credentials and password hashes): treat it as a secret.
- **REST semantics of the four fields** (`Api/RouteAccessFields.cs`): missing = keep what the route being replaced has (so matOS, which does not
  know them, cannot wipe a guard), `null` = remove, a value = replace. The old fields keep "the call describes the whole route". A header entry
  without `value` (the shape of the responses) keeps the stored value of that name. A route cannot keep upstream properties while it becomes a
  redirect (400, send `null`). Invalid input: 400 with field and reason, nothing saved.
- Downgrading Matcad loses the fields silently (unknown JSON is ignored) and the route would be open; that is why clients must check `capabilities`.
- Not modelled: the Caddyfile import ignores `header_up` on a reverse_proxy (as before); a Caddyfile guard (`not header`) stays raw passthrough.
  The header compare in Caddy is not constant-time (a 16+ character secret over a network makes that theoretical).
- Real-Caddy check (what the unit tests cannot show): `caddy:2` container started with `{"admin":{"listen":"0.0.0.0:2019"}}`, Matcad run against it
  (`CaddyAdminUrl` in `settings.json`), echo servers merged in through `RawCaddyJson` (`static_response` bodies with `{http.request.host}`,
  `{http.request.header.X-Up}`), routes created through the REST API, then curl.

**Database:** SQLite for logic, JSON for config. Tables PascalCase and singular, key `Id` (64 bit), audit columns, index and
constraint names PascalCase without underscores. Schema changes are migrations:
`dotnet ef migrations add <Name> --project src/Matcad --output-dir Data/Migrations` (tool in `dotnet-tools.json`).

**UI:** the design system of the Mat apps, **MatUi** (the toolkit of the MatDesign project; rules and live examples are on its site,
the rule ids such as SHELL-08 are in `MatDesign/src/MatDesign.Core/RuleBook.cs`). `wwwroot/matui/` is a *copy* (`matui.css`, `matui.js`,
`matui.boot.js`, `matui-icons.svg`); `scripts/update-matui.ps1` fetches a newer one from a running MatDesign, then look at every page.
The only colour Matcad chooses is the accent: `data-app="matcad"` on `<html>` (`#2f6feb`, set in `matui.css`, kept in `Ui.App.Accent`).
Pages use only toolkit classes (`mat-*`): flat surfaces, a breadcrumb above every page (`ViewData["Crumbs"] = Crumbs.Of(...)`), a title
and one line (`PageSub`), lists as toolbar / table / actions below (`_Toolbar` partial, `ToolbarModel`), a table with groups (the routes by domain) as `tbody.mat-group` with a folding heading and the add row last (LIST-16), forms as cards with the button
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
