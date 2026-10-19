using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Matcad.Config;
using Matcad.Controls;
using Matcad.Services;

namespace Matcad.Api;

/// <summary>
/// Machine-to-machine REST API so a sibling control panel (matOS, Mattiverse) can manage the
/// reverse proxy — DNS providers, routes/domains, authentications, certificates
/// and a few settings — from its own UI instead of Matcad's Razor pages.
///
/// Guarded by a shared secret in the <c>X-Api-Key</c> header, configured via
/// <c>Matcad:ApiKey</c> (env <c>Matcad__ApiKey</c>). When no key is configured the
/// API is disabled (503) so it can never be reached unauthenticated. Every mutating
/// endpoint mirrors the matching Razor page's save + <see cref="CaddyService.ApplyAsync"/>
/// behaviour, so changes take effect in Caddy immediately.
/// </summary>
public static class MatcadApi
{
    /// <summary>Version of this REST API (the <c>apiVersion</c> of <c>/status</c>). Raise it when an endpoint is added.</summary>
    public const int ApiVersion = 1;

    // Input DTOs (audit fields / ids handled by ConfigStore, never trusted from the wire).
    // ManagedBy: null (missing) = keep the stored value, "" = clear it, otherwise set it (e.g. "mattiverse").
    public record ProviderInput(long? Id, string Name, string Type, Dictionary<string, string>? Credentials,
        string? ManagedBy = null);
    public record ProviderTestInput(string Type, Dictionary<string, string>? Credentials);
    // The last four are the optional access fields. They are JsonElement because they have three states (missing = keep what is
    // stored, null = remove, a value = replace), see RouteAccessFields; a missing one stays the default element (ValueKind Undefined).
    public record RouteInput(long? Id, string Host, bool Wildcard, string? Target, string? Upstream,
        bool InsecureSkipVerify, string? FallbackUrl, bool RedirectPermanent,
        long? AuthenticationId, long? ProviderId, string? AcmeEmail, bool Enabled, string? Name,
        bool AllowEmbedding = false, int? ListenPort = null,
        JsonElement UpstreamHost = default, JsonElement UpstreamHeaders = default,
        JsonElement RequireHeader = default, JsonElement AllowedIps = default, string? ManagedBy = null);
    public record BasicUserInput(string Username, string? Password);
    public record AuthInput(long? Id, string Name, string Type, List<BasicUserInput>? Users,
        string? ManagedBy = null);
    public record SettingsInput(string? BaseDomain, string? AcmeEmail, string? MatcadHost, string? PortalMode,
        int? AcmePropagationDelaySeconds, int? AcmePropagationTimeoutSeconds, bool? SetupCompleted = null);

    /// <summary>"routeGuards": the route fields upstreamHost, upstreamHeaders, requireHeader and allowedIps.</summary>
    public static readonly string[] Capabilities = ["routeGuards"];

    public static void MapMatcadApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1")
            .AllowAnonymous() // the API key is the guard, not the admin session cookie
            .AddEndpointFilter(async (ctx, next) =>
            {
                var cfg = ctx.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
                var key = cfg["Matcad:ApiKey"];
                if (string.IsNullOrWhiteSpace(key))
                    return Problem("Matcad API is disabled: no Matcad:ApiKey configured.", 503);
                var got = ctx.HttpContext.Request.Headers["X-Api-Key"].ToString();
                if (!FixedTimeEquals(got, key))
                    return Problem("Invalid or missing X-Api-Key.", 401);
                return await next(ctx);
            });

        // ---- status ---------------------------------------------------------
        api.MapGet("/status", (ConfigStore s, RouteProvider rp) => Results.Ok(new
        {
            ok = true,
            app = Ui.App.Id,
            version = Ui.Version,
            apiVersion = ApiVersion,
            // What this Matcad understands beyond the original API. A client that wants a feature must check for it: an older
            // Matcad ignores fields it does not know, and a guard that was silently ignored leaves the route open.
            capabilities = Capabilities,
            baseDomain = s.Settings.BaseDomain,
            setupCompleted = s.Settings.SetupCompleted,
            counts = new
            {
                providers = s.Providers.Count,
                routes = s.Routes.Count,
                authentications = s.Authentications.Count,
                allRoutes = rp.All().Count
            }
        }));

        // ---- provider types (so the UI can render per-type fields incl. netcup)
        api.MapGet("/provider-types", () => Results.Ok(ProviderTypes.All.Select(t => new
        {
            id = t.Id,
            displayName = t.DisplayName,
            caddyModule = t.CaddyModule,
            fields = t.Fields.Select(f => new { key = f.Key, label = f.Label, secret = f.Secret })
        })));

        // ---- providers (DNS credentials, e.g. netcup) ------------------------
        api.MapGet("/providers", (ConfigStore s) => Results.Ok(s.Providers.Select(ToProviderDto)));
        api.MapPost("/providers", async (ConfigStore s, CaddyService caddy, ProviderInput inp) =>
        {
            if (string.IsNullOrWhiteSpace(inp.Name)) return Results.BadRequest(new { error = "Name is required." });
            if (string.IsNullOrWhiteSpace(inp.Type)) return Results.BadRequest(new { error = "Type (Caddy DNS module, e.g. netcup) is required." });
            // The type and the keys go into Caddy's JSON as a module name and object keys: refuse what could not be one.
            var credentials = inp.Credentials ?? new();
            if (ProviderRules.Check(inp.Type.Trim(), credentials.Keys) is { } invalid) return Results.BadRequest(new { error = invalid });
            var p = new ProviderConfig { Id = inp.Id ?? 0, Name = inp.Name.Trim(), Type = inp.Type.Trim(), Credentials = credentials };
            p.ManagedBy = ResolveManagedBy(inp.ManagedBy, s.Providers.FirstOrDefault(x => x.Id == p.Id && p.Id != 0));
            s.UpsertProvider(p, null);
            // A wildcard route that uses the provider needs the new credentials in Caddy (the answer keeps its shape).
            await caddy.ApplyAsync();
            return Results.Ok(ToProviderDto(p));
        });
        api.MapDelete("/providers/{id:long}", (ConfigStore s, RouteProvider rp, long id) =>
        {
            if (rp.All().Any(r => r.ProviderId == id))
                return Results.Conflict(new { error = "Provider is used by at least one route." });
            s.DeleteProvider(id);
            return Results.Ok(new { ok = true });
        });
        api.MapPost("/providers/test", async (DnsCredentialTester t, ProviderTestInput inp) =>
        {
            var (ok, message) = await t.TestAsync(inp.Type ?? "", inp.Credentials ?? new());
            return Results.Ok(new { ok, message });
        });

        // ---- authentications (basic-auth / Matcad forward-auth) --------------
        api.MapGet("/authentications", (ConfigStore s) => Results.Ok(s.Authentications.Select(a => new
        {
            id = a.Id,
            name = a.Name,
            type = a.Type.ToString(),
            users = a.Users.Select(u => u.Username),
            managedBy = a.ManagedBy
        })));
        api.MapPost("/authentications", async (ConfigStore s, CaddyService caddy, AuthInput inp) =>
        {
            if (string.IsNullOrWhiteSpace(inp.Name)) return Results.BadRequest(new { error = "Name is required." });
            if (!Enum.TryParse<AuthType>(inp.Type, ignoreCase: true, out var type)) type = AuthType.BasicAuth;
            var existing = inp.Id is > 0 ? s.Authentications.FirstOrDefault(x => x.Id == inp.Id) : null;
            var previous = (existing?.Users ?? new()).ToDictionary(u => u.Username, u => u.PasswordHash, StringComparer.OrdinalIgnoreCase);
            var users = new List<BasicAuthUser>();
            foreach (var u in inp.Users ?? new())
            {
                var name = (u.Username ?? "").Trim();
                if (name.Length == 0 || users.Any(x => x.Username.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                string hash;
                if (!string.IsNullOrEmpty(u.Password)) hash = PasswordHashing.HashOrKeep(u.Password, previous.GetValueOrDefault(name));
                else if (previous.TryGetValue(name, out var old)) hash = old; // keep existing when blank
                else continue; // new user without a password
                users.Add(new BasicAuthUser { Username = name, PasswordHash = hash });
            }
            // The API does not carry the login-page branding; an update keeps what the UI set.
            var auth = new AuthenticationConfig
            {
                Id = inp.Id is > 0 ? inp.Id.Value : 0, Name = inp.Name.Trim(), Type = type, Users = users,
                BrandTitle = existing?.BrandTitle ?? "", BrandLogo = existing?.BrandLogo ?? "",
                BrandLogoLayout = existing?.BrandLogoLayout ?? "left", BrandColor = existing?.BrandColor ?? "",
                BrandText = existing?.BrandText ?? "", ManagedBy = ResolveManagedBy(inp.ManagedBy, existing)
            };
            s.UpsertAuthentication(auth, null);
            var (ok, error) = await caddy.ApplyAsync();
            return Results.Ok(new { id = auth.Id, name = auth.Name, type = auth.Type.ToString(), managedBy = auth.ManagedBy, applied = new { ok, error } });
        });
        api.MapDelete("/authentications/{id:long}", async (ConfigStore s, CaddyService caddy, RouteProvider rp, long id) =>
        {
            if (rp.All().Any(r => r.AuthenticationId == id))
                return Results.Conflict(new { error = "Authentication is used by at least one route." });
            s.DeleteAuthentication(id);
            var (ok, error) = await caddy.ApplyAsync();
            return Results.Ok(new { ok, error });
        });

        // ---- routes / domains ------------------------------------------------
        // Overview: everything Caddy will actually serve (manual + system + docker),
        // each annotated with its certificate coverage.
        api.MapGet("/routes", (RouteProvider rp) =>
        {
            var all = rp.All();
            return Results.Ok(all.OrderBy(r => r.Host, StringComparer.OrdinalIgnoreCase).Select(r =>
            {
                var cov = CertificatePlanner.ForRoute(r, all);
                return new
                {
                    id = r.Id,
                    name = r.Name,
                    host = r.Host,
                    wildcard = r.Wildcard,
                    upstream = r.Upstream,
                    fallbackUrl = r.FallbackUrl,
                    authenticationId = r.AuthenticationId,
                    providerId = r.ProviderId,
                    enabled = r.Enabled,
                    allowEmbedding = r.AllowEmbedding,
                    listenPort = r.ListenPort,
                    upstreamHost = r.UpstreamHost,
                    upstreamHeaders = HeaderNames(r),
                    requireHeader = RequiredHeaderName(r),
                    allowedIps = r.AllowedIps,
                    managedBy = r.ManagedBy,
                    source = r.Source ?? "manual",
                    sourceDetail = r.SourceDetail,
                    editable = r.Source == null,
                    certKind = cov.Kind.ToString(),
                    certSubject = cov.Subject
                };
            }));
        });
        // Editable (manually-managed) routes only.
        api.MapGet("/routes/manual", (ConfigStore s) => Results.Ok(s.Routes.Select(ToRouteDto)));
        api.MapGet("/routes/{id:long}", (ConfigStore s, long id) =>
            s.Routes.FirstOrDefault(r => r.Id == id) is { } r ? Results.Ok(ToRouteDto(r)) : Results.NotFound(new { error = "No such route." }));
        api.MapPost("/routes", async (ConfigStore s, CaddyService caddy, RouteInput inp) =>
        {
            // Edit a copy: a rejected request must not leave half-applied changes in the stored route.
            var route = new RouteConfig { Id = inp.Id is > 0 ? inp.Id.Value : 0 };
            var form = new RouteForm(inp.Host, inp.Wildcard, string.Equals(inp.Target, "redirect", StringComparison.OrdinalIgnoreCase),
                inp.Upstream, inp.InsecureSkipVerify, inp.FallbackUrl, inp.RedirectPermanent, inp.AuthenticationId,
                inp.ProviderId, inp.AcmeEmail, inp.Enabled, inp.Name, inp.AllowEmbedding, inp.ListenPort);
            var error = RouteEditor.Apply(form, route);
            if (error != null) return Results.BadRequest(new { error });

            // The access fields are settled against the route this call is going to replace (the one with the id, else the one
            // with the same host or port): missing keeps what it has, null removes, a value replaces. Everything is then checked
            // again together with the rest, so an invalid value rejects the call and nothing is saved.
            var replaced = s.FindRouteToReplace(route);
            var accessError = RouteAccessFields.Resolve(inp, replaced, out var access);
            if (accessError != null) return Results.BadRequest(new { error = accessError });
            route = new RouteConfig { Id = route.Id };
            error = RouteEditor.Apply(form with
            {
                UpstreamHost = access.UpstreamHost, UpstreamHeaders = access.UpstreamHeaders,
                RequireHeader = access.RequireHeader, AllowedIps = access.AllowedIps,
                // The API does not carry the options of the edit page (blocked IPs, HSTS, ...): the route keeps what it has.
                Options = replaced is null ? null : RouteEditor.OptionsOf(replaced)
            }, route);
            if (error != null) return Results.BadRequest(new { error });
            route.ManagedBy = ResolveManagedBy(inp.ManagedBy, replaced);

            // A host (or a port) belongs to one route. Without an id the call means "make sure this route exists",
            // so it updates the route that already has the host; with an id it must not take over another host.
            if (!s.TryUpsertRoute(route, null, updateSameTarget: true))
                return Results.Conflict(new { error = "Another route already uses this host or port." });
            var (ok, applyError) = await caddy.ApplyAsync();
            return Results.Ok(new { route = ToRouteDto(route), applied = new { ok, error = applyError } });
        });
        api.MapDelete("/routes/{id:long}", async (ConfigStore s, CaddyService caddy, long id) =>
        {
            s.DeleteRoute(id);
            var (ok, error) = await caddy.ApplyAsync();
            return Results.Ok(new { ok, error });
        });

        // ---- certificates (what Caddy will actually manage) ------------------
        api.MapGet("/certificates", (RouteProvider rp, ConfigStore s) =>
        {
            var plan = CertificatePlanner.Plan(rp.All(), s.Providers);
            return Results.Ok(new
            {
                wildcards = plan.Wildcards.Select(w => new
                {
                    host = w.WildcardHost,
                    subjects = w.Subjects,
                    provider = w.Provider,
                    coveredHosts = w.CoveredHosts
                }),
                individual = plan.Individual,
                internalHosts = plan.Internal
            });
        });

        // ---- settings (the handful matOS surfaces) ---------------------------
        api.MapGet("/settings", (ConfigStore s) =>
        {
            var x = s.Settings;
            return Results.Ok(new
            {
                baseDomain = x.BaseDomain,
                acmeEmail = x.AcmeEmail,
                matcadHost = x.MatcadHost,
                portalMode = x.PortalMode,
                acmePropagationDelaySeconds = x.AcmePropagationDelaySeconds,
                acmePropagationTimeoutSeconds = x.AcmePropagationTimeoutSeconds,
                setupCompleted = x.SetupCompleted
            });
        });
        api.MapPut("/settings", async (ConfigStore s, CaddyService caddy, SettingsInput inp) =>
        {
            var x = s.Settings;
            // Check everything first, so a rejected call changes nothing.
            foreach (var domain in new[] { inp.BaseDomain, inp.MatcadHost })
                if (!string.IsNullOrWhiteSpace(domain) && !RouteEditor.IsHostName(domain.Trim()))
                    return Results.BadRequest(new { error = "Domains must be plain names like example.com (no http://, path or port)." });
            if (inp.PortalMode != null && inp.PortalMode.Trim().ToLowerInvariant() is not ("inline" or "redirect" or "unauthorized"))
                return Results.BadRequest(new { error = "PortalMode must be inline, redirect or unauthorized." });

            if (inp.BaseDomain != null) x.BaseDomain = inp.BaseDomain.Trim().ToLowerInvariant();
            if (inp.AcmeEmail != null) x.AcmeEmail = inp.AcmeEmail.Trim();
            if (inp.MatcadHost != null) x.MatcadHost = inp.MatcadHost.Trim().ToLowerInvariant();
            if (inp.PortalMode != null) x.PortalMode = inp.PortalMode.Trim().ToLowerInvariant();
            if (inp.AcmePropagationDelaySeconds.HasValue) x.AcmePropagationDelaySeconds = inp.AcmePropagationDelaySeconds.Value;
            if (inp.AcmePropagationTimeoutSeconds.HasValue) x.AcmePropagationTimeoutSeconds = inp.AcmePropagationTimeoutSeconds.Value;
            // A headless install (Mattiverse) has done what the first-run wizard would ask for.
            if (inp.SetupCompleted.HasValue) x.SetupCompleted = inp.SetupCompleted.Value;
            s.SaveSettings(x);
            var (ok, error) = await caddy.ApplyAsync();
            return Results.Ok(new { ok, error, setupCompleted = x.SetupCompleted });
        });
    }

    // The usual problem-details body plus the "error" member that every other error of this API has.
    private static IResult Problem(string message, int status) =>
        Results.Problem(message, statusCode: status, extensions: new Dictionary<string, object?> { ["error"] = message });

    /// <summary>The <c>managedBy</c> to store: missing (null) keeps what the object being replaced has, blank clears it,
    /// anything else is set (trimmed). So a client that does not know the field never removes the mark.</summary>
    private static string? ResolveManagedBy(string? requested, ConfigEntity? replaced) =>
        requested == null ? replaced?.ManagedBy
        : string.IsNullOrWhiteSpace(requested) ? null
        : requested.Trim();

    private static object ToProviderDto(ProviderConfig p) => new
    {
        id = p.Id,
        name = p.Name,
        type = p.Type,
        credentials = p.Credentials,
        managedBy = p.ManagedBy
    };

    private static object ToRouteDto(RouteConfig r) => new
    {
        id = r.Id,
        name = r.Name,
        host = r.Host,
        wildcard = r.Wildcard,
        upstream = r.Upstream,
        insecureSkipVerify = r.InsecureSkipVerify,
        fallbackUrl = r.FallbackUrl,
        redirectPermanent = r.RedirectPermanent,
        authenticationId = r.AuthenticationId,
        providerId = r.ProviderId,
        acmeEmail = r.AcmeEmail,
        enabled = r.Enabled,
        allowEmbedding = r.AllowEmbedding,
        listenPort = r.ListenPort,
        // Write-only: the host and the addresses are shown as they are, but a header only says that it has a value. That is
        // enough for a client to check that a guard was really stored, and keeps the secret from ever coming back out.
        upstreamHost = r.UpstreamHost,
        upstreamHeaders = HeaderNames(r),
        requireHeader = RequiredHeaderName(r),
        allowedIps = r.AllowedIps,
        managedBy = r.ManagedBy
    };

    private static object[] HeaderNames(RouteConfig r) =>
        r.UpstreamHeaders.Select(h => (object)new { name = h.Name, valueSet = !string.IsNullOrEmpty(h.Value) }).ToArray();

    private static object? RequiredHeaderName(RouteConfig r) =>
        r.RequireHeader is { } h ? new { name = h.Name, valueSet = !string.IsNullOrEmpty(h.Value) } : null;

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a ?? "");
        var bb = Encoding.UTF8.GetBytes(b ?? "");
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}
