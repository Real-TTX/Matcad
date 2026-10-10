using System.Security.Cryptography;
using System.Text;
using Matcad.Config;
using Matcad.Services;

namespace Matcad.Api;

/// <summary>
/// Machine-to-machine REST API so a sibling control panel (matOS) can manage the
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
    // Input DTOs (audit fields / ids handled by ConfigStore, never trusted from the wire).
    public record ProviderInput(long? Id, string Name, string Type, Dictionary<string, string>? Credentials);
    public record ProviderTestInput(string Type, Dictionary<string, string>? Credentials);
    public record RouteInput(long? Id, string Host, bool Wildcard, string? Target, string? Upstream,
        bool InsecureSkipVerify, string? FallbackUrl, bool RedirectPermanent,
        long? AuthenticationId, long? ProviderId, string? AcmeEmail, bool Enabled, string? Name,
        bool AllowEmbedding = false, int? ListenPort = null);
    public record BasicUserInput(string Username, string? Password);
    public record AuthInput(long? Id, string Name, string Type, List<BasicUserInput>? Users);
    public record SettingsInput(string? BaseDomain, string? AcmeEmail, string? MatcadHost, string? PortalMode,
        int? AcmePropagationDelaySeconds, int? AcmePropagationTimeoutSeconds);

    public static void MapMatcadApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1")
            .AllowAnonymous() // the API key is the guard, not the admin session cookie
            .AddEndpointFilter(async (ctx, next) =>
            {
                var cfg = ctx.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
                var key = cfg["Matcad:ApiKey"];
                if (string.IsNullOrWhiteSpace(key))
                    return Results.Problem("Matcad API is disabled: no Matcad:ApiKey configured.", statusCode: 503);
                var got = ctx.HttpContext.Request.Headers["X-Api-Key"].ToString();
                if (!FixedTimeEquals(got, key))
                    return Results.Problem("Invalid or missing X-Api-Key.", statusCode: 401);
                return await next(ctx);
            });

        // ---- status ---------------------------------------------------------
        api.MapGet("/status", (ConfigStore s, RouteProvider rp) => Results.Ok(new
        {
            ok = true,
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
        api.MapPost("/providers", (ConfigStore s, ProviderInput inp) =>
        {
            if (string.IsNullOrWhiteSpace(inp.Name)) return Results.BadRequest(new { error = "Name is required." });
            if (string.IsNullOrWhiteSpace(inp.Type)) return Results.BadRequest(new { error = "Type (Caddy DNS module, e.g. netcup) is required." });
            var p = new ProviderConfig { Id = inp.Id ?? 0, Name = inp.Name.Trim(), Type = inp.Type.Trim(), Credentials = inp.Credentials ?? new() };
            s.UpsertProvider(p, null);
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
            users = a.Users.Select(u => u.Username)
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
                BrandText = existing?.BrandText ?? ""
            };
            s.UpsertAuthentication(auth, null);
            var (ok, error) = await caddy.ApplyAsync();
            return Results.Ok(new { id = auth.Id, name = auth.Name, type = auth.Type.ToString(), applied = new { ok, error } });
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
        api.MapPost("/routes", async (ConfigStore s, CaddyService caddy, RouteInput inp) =>
        {
            // Edit a copy: a rejected request must not leave half-applied changes in the stored route.
            var route = new RouteConfig { Id = inp.Id is > 0 ? inp.Id.Value : 0 };
            var error = RouteEditor.Apply(
                new RouteForm(inp.Host, inp.Wildcard, string.Equals(inp.Target, "redirect", StringComparison.OrdinalIgnoreCase),
                    inp.Upstream, inp.InsecureSkipVerify, inp.FallbackUrl, inp.RedirectPermanent, inp.AuthenticationId,
                    inp.ProviderId, inp.AcmeEmail, inp.Enabled, inp.Name, inp.AllowEmbedding, inp.ListenPort),
                route);
            if (error != null) return Results.BadRequest(new { error });

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
                acmePropagationTimeoutSeconds = x.AcmePropagationTimeoutSeconds
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
            s.SaveSettings(x);
            var (ok, error) = await caddy.ApplyAsync();
            return Results.Ok(new { ok, error });
        });
    }

    private static object ToProviderDto(ProviderConfig p) => new
    {
        id = p.Id,
        name = p.Name,
        type = p.Type,
        credentials = p.Credentials
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
        listenPort = r.ListenPort
    };

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a ?? "");
        var bb = Encoding.UTF8.GetBytes(b ?? "");
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}
