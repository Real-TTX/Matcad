# Review 2026-10

Branch `feature/review` (from `dev`). Every page was opened locally (Development, temp data dir) in light, dark and at 390 px with
Edge/Playwright; the new route options and the certificate check also ran against a throwaway `caddy:2` container. Not exercised:
real ACME issuance and renewal (no public DNS), Docker discovery (no socket mounted), sign-in through MatIdentity.

## Findings and fixes

| # | Finding | Fix |
|---|---|---|
| 1 | **Secret in the page:** the DNS provider page wrote stored API keys/passwords into `value=` of the inputs (custom credentials even as plain text). | Stored secrets never reach the HTML; the field says "Saved - leave empty to keep", an empty field keeps the value. Shared by provider page and setup wizard. |
| 2 | **Secret in the page:** Settings -> Caddy showed DNS credentials and basic-auth hashes of the generated and the live config. | `SecretRedaction` hides them. |
| 3 | **Placeholder injection:** a redirect target went into `Location` unchanged; Caddy fills in placeholders, so `https://x/{env.SECRET}` or `{file.*}` handed out Caddy's environment and files (any signed-in user, any API key holder). | Braces, spaces and control characters are refused; a stored target reaches Caddy percent-encoded. |
| 4 | **JSON key injection:** a provider credential called `name` replaced the DNS module; type and keys were unchecked. | `ProviderRules`: module `a-z0-9_`, keys `A-Za-z0-9_`, `name` reserved (page, API, generator). |
| 5 | Saving a DNS provider did not push the config: new credentials reached Caddy only with the next unrelated change. | Page and API push after saving. |
| 6 | Unchecked inputs: logo address of the login page (any scheme), login portal address (any text, used for redirects), Docker base domain. | Logo: http(s), a path or an embedded image; portal: an http(s) origin; Docker domain: a host name. |
| 7 | Owner's rule broken: dashboard and statistics held `MatcadDbContext`. | Queries moved to `Services/RequestLogQueries`; a test keeps every page model off the context. |
| 8 | A failed push to Caddy was only visible as a toast after the save; the dashboard looked fine while Caddy ran an old config. | The dashboard shows Caddy's error with a link to the settings. |
| 9 | Certificates were a tab in admin settings listing only the plan; the expiry the monitor reads every six hours was only in the platform tiles. | Own page **Certificates** (menu Proxy): expiry, days left, state of the last TLS check, problems first, search, filter, *Check now*. Old tab link redirects. |
| 10 | Wordy texts (hints of three lines, sub-titles explaining the obvious, duplicate "Requests in total" tile). | Shortened on route, provider, authentication, settings, import, dashboard; the tile shows active routes. |
| 11 | The plain 401 of the login portal carried an inline style. | Removed. |
| 12 | **Startup hang:** EF Core 10 on SQLite leaves a row in `__EFMigrationsLock` when the process is stopped while it migrates (an update recreates the container seconds after the start); every later start waited for it forever. | `MigrationLock.ReleaseStaleAsync` (ported from MatDock) removes it before `MigrateAsync`, with a warning in the log. The backup restore does not migrate, so it needs nothing. |
| 13 | The header-name check ended in `$`, which also matches before a final line break (a stored, untrimmed name). | `\z`. |

Kept as is (deliberate): `GET /api/v1/providers` returns credentials in clear to the API key holder - MatOS edits providers through it and
`/api/v1` must stay byte-compatible. Should a v2 ever come: `credentialsSet` instead (as Mattiverse's own contract already does).

## Gap list against Nginx Proxy Manager (+ what Caddy managers offer)

| Feature | Matcad | Note / plan |
|---|---|---|
| Proxy hosts | has | routes, domain tree |
| SSL per host, Let's Encrypt (HTTP challenge) | has | automatic through Caddy |
| DNS challenge, wildcard certificates | has | 8 providers + custom module |
| Credential test | has | netcup, **Cloudflare (new)** |
| Certificate list with expiry | **has (new)** | Certificates page |
| Renew a certificate by hand | dropped | Caddy renews on its own at a third of the lifetime; the page shows whether it did |
| Redirection hosts | has | 301/302, **keep the path (new)** |
| Redirect codes 307/308 | **has (new)** | per route on the route page (308/307 keep the method); the API keeps the stored choice |
| Streams (TCP/UDP forwarding) | partly | port bindings (HTTP only, API). Raw TCP/UDP needs the caddy-l4 plugin: L, plan only if asked |
| 404 hosts / default site | **has (new)** | Settings -> General -> Unknown hosts: Caddy default, 404 page, redirect or closed connection (plain HTTP; HTTPS for an unknown name fails at the handshake) |
| Access lists: basic auth | has | authentications (reusable), plus the Matcad login portal |
| Access lists: IP allow | has | allowed IPs |
| Access lists: IP deny | **has (new)** | blocked IPs |
| Reusable access lists (auth + IPs as one object) | partly | IPs are per route; M - only if many routes share lists |
| Custom locations (per-path upstreams) | missing | M - `path` matcher per sub-route; next candidate |
| Websockets | has | Caddy proxies them without a switch |
| HTTP/2, HTTP/3 | has | Caddy default |
| HSTS | **has (new)** | per route |
| Block common exploits | **has (new)** | per route |
| Custom headers | **has (new)** | to the upstream (incl. Host) and, **new**, set/remove on the answer per route (checked against header injection and placeholders) |
| Custom config | has | extra Caddy JSON |
| Users and permissions | has | admin / user, MatIdentity |
| Audit log | **has (new)** | Settings -> Audit log |
| Access log per host | has | statistics filtered by host (latest 100), **link from the routes list (new)**, live log on the dashboard |
| Upstream health | partly | 502/504 hosts in the platform tile; active health checks: M together with load balancing |
| Load balancing over several upstreams | missing | M - list of upstreams + policy; needs an additive API field (`upstreams`) next to `upstream` |
| Compression | **has (new)** | per route |
| Docker discovery, Caddyfile import, backup | has | beyond NPM |

### Plan (by value)

1. Custom locations (M): path rules per route, each with its own upstream or redirect.
2. Several upstreams with load balancing and active health checks (M), API field added, never changed.

Done since the review: response headers per route, redirect codes 307/308, unknown hosts (404 / default host), stale migration lock. All three
route features were checked against a real caddy:2 with Matcad driving it (headers replaced and removed over the upstream, 307/308 with the kept
path, the 404 page for an unknown host on plain HTTP; the redirect and the closed connection with the same JSON loaded by hand); that the answer stays behind the routes of the extra
JSON is a unit test.

## Tests

Before: 432. After the review: 489. After the follow-ups (migration lock, redirect codes, response headers, unknown hosts): 549, all green;
`dotnet build` 0 warnings.
