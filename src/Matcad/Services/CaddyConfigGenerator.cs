using Matcad.Config;

namespace Matcad.Services;

/// <summary>
/// Builds a complete Caddy JSON config from the stored providers, routes and
/// authentications. The result is pushed to Caddy's admin API (/load).
///
/// Structure produced:
///   admin        -> keep the admin API reachable on the docker network
///   logging      -> JSON access log to the shared volume (Matcad ingests it)
///   apps.http    -> one route per enabled Matcad route (host matcher +
///                   optional guard + auth handler + reverse_proxy / fallback redirect)
///   apps.tls     -> ACME DNS-01 automation policy per wildcard route
/// </summary>
public class CaddyConfigGenerator
{
    private readonly ConfigStore _store;
    private readonly RouteProvider _routes;
    private readonly IConfiguration _cfg;

    public CaddyConfigGenerator(ConfigStore store, RouteProvider routes, IConfiguration cfg)
    {
        _store = store;
        _routes = routes;
        _cfg = cfg;
    }

    public string AccessLogPath =>
        (_cfg["Matcad:CaddyLogDir"] ?? "/caddy-logs").TrimEnd('/') + "/access.log";

    /// <summary>The URL Caddy uses to reach Matcad for forward-auth verification.</summary>
    private string MatcadUpstream => _cfg["Matcad:SelfUpstream"] ?? "matcad:4433";

    public object Build()
    {
        var settings = _store.Settings;
        // Manual routes merged with Docker-derived routes (manual wins on collision).
        var enabled = _routes.All().Where(r => r.Enabled).ToList();
        // Host-matched routes (served by the main :80/:443 server) vs. port-bound routes
        // (each gets its own listener on a dedicated port — DNS-free, used by matOS embedding).
        var routes = enabled.Where(r => !(r.ListenPort > 0) && !string.IsNullOrWhiteSpace(r.Host)).ToList();
        var portRoutes = enabled.Where(r => r.ListenPort > 0 && !string.IsNullOrWhiteSpace(r.Upstream)).ToList();

        // Specific hosts must be matched before wildcards.
        var ordered = routes.OrderBy(r => r.Wildcard ? 1 : 0).ThenBy(r => r.Host).ToList();

        // The login page is served ON the protected host (via a /auth/* bypass route)
        // whenever the verify endpoint issues a RELATIVE redirect to /auth/portal:
        // that is inline mode, and also redirect mode when no portal URL resolves
        // (Verify falls back to relative). Without the bypass route that fallback
        // would loop forever, so keep the two in sync.
        var mode = (settings.PortalMode ?? "inline").ToLowerInvariant();
        var serveAuthLocally = mode != "unauthorized"
            && (mode != "redirect" || string.IsNullOrWhiteSpace(settings.EffectivePortalUrl()));
        var httpRoutes = new List<object>();
        foreach (var r in ordered)
        {
            // Serve /auth/* on the protected host itself (proxied to Matcad,
            // unprotected) so the login never leaves the host.
            if (serveAuthLocally && UsesMatcadAuth(r))
                httpRoutes.Add(BuildAuthPathRoute(r));
            httpRoutes.Add(BuildRoute(r));
        }

        var servers = new Dictionary<string, object?>
        {
            ["matcad"] = new Dictionary<string, object?>
            {
                ["listen"] = new[] { ":80", ":443" },
                ["routes"] = httpRoutes,
                // Route this server's access logs to the "matcad" logger so the file log captures them.
                ["logs"] = new Dictionary<string, object?> { ["default_logger_name"] = "matcad" }
            }
        };

        // Port-bound routes: one server per port, listening on :<port>, no host matcher — reachable
        // DNS-free as http://<host>:<port>. matOS uses these so embedded apps work over a bare
        // hostname/IP where a wildcard app domain can't resolve.
        foreach (var pr in portRoutes)
        {
            servers[$"port_{pr.ListenPort}"] = new Dictionary<string, object?>
            {
                ["listen"] = new[] { $":{pr.ListenPort}" },
                ["routes"] = new object[]
                {
                    new Dictionary<string, object?> { ["handle"] = BuildHandlers(pr) }
                }
            };
        }

        var config = new Dictionary<string, object?>
        {
            ["admin"] = new Dictionary<string, object?> { ["listen"] = "0.0.0.0:2019" },
            ["logging"] = new Dictionary<string, object?>
            {
                ["logs"] = new Dictionary<string, object?>
                {
                    ["access"] = new Dictionary<string, object?>
                    {
                        ["writer"] = new Dictionary<string, object?>
                        {
                            ["output"] = "file",
                            ["filename"] = AccessLogPath,
                            ["roll"] = true,
                            ["roll_size_mb"] = 10,
                            ["roll_keep"] = 5
                        },
                        ["encoder"] = BuildAccessEncoder(enabled),
                        ["include"] = new[] { "http.log.access.matcad" }
                    }
                }
            },
            ["apps"] = new Dictionary<string, object?>
            {
                ["http"] = new Dictionary<string, object?>
                {
                    ["servers"] = servers
                }
            }
        };

        var tls = BuildTls(routes, settings);
        if (tls != null)
            ((Dictionary<string, object?>)config["apps"]!)["tls"] = tls;

        return config;
    }

    /// <summary>True when the route is protected by a Matcad forward-auth (which
    /// needs the /auth/* endpoints served on the protected host in inline mode).</summary>
    private bool UsesMatcadAuth(RouteConfig route) =>
        route.AuthenticationId is > 0 &&
        _store.Authentications.FirstOrDefault(a => a.Id == route.AuthenticationId)?.Type == AuthType.Matcad;

    /// <summary>Inline portal: an unprotected route on the same host that proxies
    /// /auth/* to Matcad, so the login page is served from the protected host
    /// itself and the Matcad host is never revealed. Placed before the protected
    /// route so the login endpoints are not caught by forward-auth.</summary>
    private Dictionary<string, object?> BuildAuthPathRoute(RouteConfig route)
    {
        var (dial, _) = ParseDial(MatcadUpstream);
        // A guarded host is closed to everything that does not pass the guard, the login pages included.
        var handlers = new List<object>();
        var guard = route.HasGuards ? BuildGuard(route) : null;
        if (route.HasGuards && guard == null)
            handlers.Add(Unavailable(GuardInvalid)); // what the guard could not close the login pages must not open
        else
        {
            if (guard != null) handlers.Add(guard);
            handlers.Add(new Dictionary<string, object?>
            {
                ["handler"] = "reverse_proxy",
                ["upstreams"] = new[] { new Dictionary<string, object?> { ["dial"] = dial } }
            });
        }
        return new Dictionary<string, object?>
        {
            ["match"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["host"] = new[] { route.Host },
                    ["path"] = new[] { "/auth/*" }
                }
            },
            ["handle"] = handlers,
            ["terminal"] = true
        };
    }

    private Dictionary<string, object?> BuildRoute(RouteConfig route) => new()
    {
        ["match"] = new[] { new Dictionary<string, object?> { ["host"] = new[] { route.Host } } },
        ["handle"] = BuildHandlers(route),
        ["terminal"] = true
    };

    private const string GuardInvalid = "Matcad: the access guard of this route is invalid, so the route is not served.";

    private static Dictionary<string, object?> Unavailable(string message) => new()
    {
        ["handler"] = "static_response",
        ["status_code"] = 503,
        ["body"] = message
    };

    /// <summary>
    /// The guard of a route as a Caddy handler, or null when it cannot be built. A request is refused with 403 (and no body:
    /// nothing about what was expected) when its direct peer is not in the allowed addresses, or when it lacks the required
    /// header. Each refusal is a route of its own in a subroute, so a request has to get past both, and one that passes
    /// falls through to the rest of the chain.
    ///
    /// Caddy's <c>not</c> matcher matches when none of its matcher sets match; <c>remote_ip</c> looks at the TCP peer
    /// (X-Forwarded-For plays no part), and the header matcher compares the exact value (the validation keeps <c>*</c>
    /// and placeholders out of it).
    /// </summary>
    private static object? BuildGuard(RouteConfig route)
    {
        try
        {
            var refusals = new List<object>();
            if (route.AllowedIps.Count > 0)
            {
                var (ips, error) = RouteGuards.NormalizeIps(route.AllowedIps);
                // An unusable list must not turn into "no list": that would leave the route open.
                if (error != null || ips.Count == 0) return null;
                refusals.Add(Refusal(new Dictionary<string, object?> { ["remote_ip"] = new Dictionary<string, object?> { ["ranges"] = ips } }));
            }
            if (route.BlockedIps.Count > 0)
            {
                var (blocked, error) = RouteGuards.NormalizeIps(route.BlockedIps, RouteGuards.BlockedLabel);
                if (error != null || blocked.Count == 0) return null;
                refusals.Add(Refuse(new Dictionary<string, object?> { ["remote_ip"] = new Dictionary<string, object?> { ["ranges"] = blocked } }));
            }
            if (route.RequireHeader != null)
            {
                if (RouteGuards.CheckRequireHeader(route.RequireHeader) != null) return null;
                refusals.Add(Refusal(new Dictionary<string, object?>
                {
                    ["header"] = new Dictionary<string, object?>
                    {
                        [RouteGuards.CanonicalName(route.RequireHeader.Name)] = new[] { route.RequireHeader.Value }
                    }
                }));
            }
            return refusals.Count == 0 ? null : new Dictionary<string, object?> { ["handler"] = "subroute", ["routes"] = refusals };
        }
        catch
        {
            return null; // fail closed, see the caller
        }
    }

    /// <summary>403 for a request that does NOT match (an allow list, a required header).</summary>
    private static Dictionary<string, object?> Refusal(Dictionary<string, object?> matcher) =>
        Refuse(new Dictionary<string, object?> { ["not"] = new[] { matcher } });

    /// <summary>403 (no body) for a request that matches one of the matcher sets.</summary>
    private static Dictionary<string, object?> Refuse(params Dictionary<string, object?>[] matchers) => new()
    {
        ["match"] = matchers,
        ["handle"] = new object[] { new Dictionary<string, object?> { ["handler"] = "static_response", ["status_code"] = 403 } },
        ["terminal"] = true
    };

    // What "block common exploits" refuses. Kept to what a normal site never sends: hidden folders of tools and files with secrets in
    // the path, and in the query SQL injection, script tags, PHP superglobals and path traversal (also percent-encoded).
    internal const string ExploitPathPattern = @"(?i)/\.(git|svn|hg|env|htaccess|htpasswd|ds_store)(/|$)|/(wp-config\.php|etc/passwd|proc/self/environ)$";
    internal const string ExploitQueryPattern =
        @"(?i)(union(\s|\+|%20)+(all(\s|\+|%20)+)?select|concat(\s|%20)*\(|base64_(en|de)code(\s|%20)*\(|(<|%3c)(\s|%20)*script|(globals|_request)(=|\[|%5b)|proc/self/environ|etc/passwd|\.\./|\.\.%2f|%2e%2e(/|%2f))";

    /// <summary>The "block common exploits" filter: 403 for a request whose path or query looks like an attack.</summary>
    private static Dictionary<string, object?> ExploitFilter() => new()
    {
        ["handler"] = "subroute",
        ["routes"] = new[]
        {
            Refuse(
                new Dictionary<string, object?> { ["path_regexp"] = new Dictionary<string, object?> { ["pattern"] = ExploitPathPattern } },
                // A CEL raw string takes the pattern as it is; Caddy turns the placeholder into a lookup of the query.
                new Dictionary<string, object?> { ["expression"] = "{http.request.uri.query}.matches(r'" + ExploitQueryPattern + "')" })
        }
    };

    /// <summary>The encoder of the access log. The log is a file on a shared volume, and Caddy writes the request headers
    /// into it: the value of a required header is a secret, so it is cut out (Matcad's own ingest only reads host, path,
    /// method, status, client address and duration into its database).</summary>
    private static Dictionary<string, object?> BuildAccessEncoder(IEnumerable<RouteConfig> enabled)
    {
        var json = new Dictionary<string, object?> { ["format"] = "json" };
        var names = enabled.Where(r => r.RequireHeader is { Name.Length: > 0 })
            .Select(r => RouteGuards.CanonicalName(r.RequireHeader!.Name)).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (names.Count == 0) return json;
        return new Dictionary<string, object?>
        {
            ["format"] = "filter",
            ["wrap"] = json,
            ["fields"] = names.ToDictionary(
                n => $"request>headers>{n}",
                n => (object?)new Dictionary<string, object?> { ["filter"] = "delete" })
        };
    }

    /// <summary>The handler chain for a route (guard → framing-header strip → auth → reverse_proxy/redirect).
    /// Shared by host-matched routes and port-bound listener servers, so a guard holds wherever the chain is used.</summary>
    private List<object> BuildHandlers(RouteConfig route)
    {
        // The guard comes first, before authentication, the framing strip or the proxy: a refused request gets nothing else to look at.
        var guarded = new List<object>();
        if (route.HasGuards)
        {
            // Fail closed: a route that asks for a guard is never served open, whatever is wrong with it.
            var guard = BuildGuard(route);
            if (guard == null) return [Unavailable(GuardInvalid)];
            guarded.Add(guard);
        }
        // The same for the rest of the access settings (a restored backup or a hand-edited file can carry values the editor would refuse).
        if (RouteGuards.Check(route) != null)
            return [.. guarded, Unavailable("Matcad: the access settings of this route are invalid, so the route is not served.")];

        var handlers = new List<object>(guarded);

        if (route.BlockExploits) handlers.Add(ExploitFilter());

        // HSTS on every answer of the host (a login or an error too), so the browser keeps to HTTPS.
        if (route.Hsts)
            handlers.Add(new Dictionary<string, object?>
            {
                ["handler"] = "headers",
                ["response"] = new Dictionary<string, object?>
                {
                    ["set"] = new Dictionary<string, object?> { ["Strict-Transport-Security"] = new[] { "max-age=31536000" } }
                }
            });

        // Optional: strip framing headers so the site can be embedded in an iframe (e.g. matOS).
        // Deferred so it runs after the upstream sets them; placed first so it wraps the response.
        if (route.AllowEmbedding)
            handlers.Add(new Dictionary<string, object?>
            {
                ["handler"] = "headers",
                ["response"] = new Dictionary<string, object?>
                {
                    ["deferred"] = true,
                    ["delete"] = new[] { "X-Frame-Options" },
                    ["replace"] = new Dictionary<string, object?>
                    {
                        ["Content-Security-Policy"] = new[]
                        {
                            new Dictionary<string, object?> { ["search_regexp"] = "frame-ancestors[^;]*;?\\s*", ["replace"] = "" }
                        }
                    }
                }
            });

        if (route.AuthenticationId is > 0)
        {
            var auth = _store.Authentications.FirstOrDefault(a => a.Id == route.AuthenticationId);
            // Fail closed: a route that asks for a login must never be served without one, whatever went wrong
            // with the authentication (deleted, restored from a backup without it, no users).
            if (auth == null || (auth.Type == AuthType.BasicAuth && auth.Users.Count == 0))
                return [.. guarded, Unavailable("Matcad: the authentication of this route is missing or has no users, so the route is not served.")];
            handlers.AddRange(BuildAuthHandlers(auth));
        }

        // Compression wraps what the upstream answers (encode compresses only types and sizes worth it).
        if (route.Compress && !string.IsNullOrWhiteSpace(route.Upstream))
            handlers.Add(new Dictionary<string, object?>
            {
                ["handler"] = "encode",
                ["encodings"] = new Dictionary<string, object?> { ["zstd"] = new Dictionary<string, object?>(), ["gzip"] = new Dictionary<string, object?>() },
                ["prefer"] = new[] { "zstd", "gzip" }
            });

        handlers.Add(BuildTerminalHandler(route));
        return handlers;
    }

    /// <summary>reverse_proxy to the upstream, else redirect to the fallback URL.</summary>
    private object BuildTerminalHandler(RouteConfig route)
    {
        if (!string.IsNullOrWhiteSpace(route.Upstream))
        {
            var (dial, isHttps) = ParseDial(route.Upstream!);
            var proxy = new Dictionary<string, object?>
            {
                ["handler"] = "reverse_proxy",
                ["upstreams"] = new[] { new Dictionary<string, object?> { ["dial"] = dial } }
            };
            var host = RouteGuards.NormalizeUpstreamHost(route.UpstreamHost).Host;
            if (isHttps)
            {
                var tls = new Dictionary<string, object?>();
                if (route.InsecureSkipVerify) tls["insecure_skip_verify"] = true;
                // The name asked for is also the name the certificate has to carry (and the one the upstream picks its certificate by).
                if (host != null) tls["server_name"] = RouteGuards.TlsServerName(host);
                proxy["transport"] = new Dictionary<string, object?>
                {
                    ["protocol"] = "http",
                    ["tls"] = tls
                };
            }

            // Headers to the upstream. Caddy sets a header under exactly the key it is given, so the names are written the way
            // the HTTP stack writes them - otherwise "x-foo" would sit next to the "X-Foo" the visitor sent instead of replacing it.
            var set = new Dictionary<string, object?>();
            if (host != null) set["Host"] = new[] { host };
            foreach (var h in route.UpstreamHeaders) set[RouteGuards.CanonicalName(h.Name)] = new[] { h.Value };
            // The secret of a required header was for this proxy: the website behind it (and any plugin or log there) must not see it.
            // Caddy deletes after it sets, so the validation keeps an upstream header of the same name out (RouteGuards.CheckNameClash).
            var delete = new List<string>();
            if (route.RequireHeader is { Name.Length: > 0 } required)
                delete.Add(RouteGuards.CanonicalName(required.Name));
            if (set.Count > 0 || delete.Count > 0)
            {
                var request = new Dictionary<string, object?>();
                if (set.Count > 0) request["set"] = set;
                if (delete.Count > 0) request["delete"] = delete;
                proxy["headers"] = new Dictionary<string, object?> { ["request"] = request };
            }
            return proxy;
        }

        if (!string.IsNullOrWhiteSpace(route.FallbackUrl))
        {
            return new Dictionary<string, object?>
            {
                ["handler"] = "static_response",
                ["status_code"] = route.RedirectPermanent ? 301 : 302,
                ["headers"] = new Dictionary<string, object?>
                {
                    ["Location"] = new[] { RedirectLocation(route.FallbackUrl!, route.RedirectKeepPath) }
                }
            };
        }

        return new Dictionary<string, object?>
        {
            ["handler"] = "static_response",
            ["status_code"] = 503,
            ["body"] = "Matcad: no upstream configured for this route."
        };
    }

    /// <summary>
    /// Auth handler chain for a route. BasicAuth -> Caddy http_basic.
    /// Matcad -> forward_auth style verification against Matcad's /auth/verify
    /// (fully wired in M6).
    /// </summary>
    private IEnumerable<object> BuildAuthHandlers(AuthenticationConfig auth)
    {
        if (auth.Type == AuthType.BasicAuth)
        {
            var accounts = auth.Users.Select(u => new Dictionary<string, object?>
            {
                ["username"] = u.Username,
                // Caddy expects the base64-encoded hashed password bytes.
                ["password"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(u.PasswordHash))
            }).ToList();

            yield return new Dictionary<string, object?>
            {
                ["handler"] = "authentication",
                ["providers"] = new Dictionary<string, object?>
                {
                    ["http_basic"] = new Dictionary<string, object?>
                    {
                        ["hash"] = new Dictionary<string, object?> { ["algorithm"] = "bcrypt" },
                        ["accounts"] = accounts
                    }
                }
            };
        }
        else if (auth.Type == AuthType.Matcad)
        {
            // forward_auth: ask Matcad to verify the session; on 2xx continue,
            // otherwise Matcad responds with a redirect to its login portal.
            yield return new Dictionary<string, object?>
            {
                ["handler"] = "reverse_proxy",
                ["upstreams"] = new[] { new Dictionary<string, object?> { ["dial"] = MatcadUpstream } },
                ["rewrite"] = new Dictionary<string, object?>
                {
                    ["method"] = "GET",
                    ["uri"] = "/auth/verify"
                },
                ["headers"] = new Dictionary<string, object?>
                {
                    ["request"] = new Dictionary<string, object?>
                    {
                        ["set"] = new Dictionary<string, object?>
                        {
                            ["X-Forwarded-Method"] = new[] { "{http.request.method}" },
                            ["X-Forwarded-Uri"] = new[] { "{http.request.uri}" },
                            ["X-Forwarded-Host"] = new[] { "{http.request.hostport}" }, // with the port, so the login sends the user back to the same address
                            // Tells /auth/verify which authentication (and portal) applies.
                            ["X-Matcad-Auth"] = new[] { auth.Id.ToString() }
                        }
                    }
                },
                ["handle_response"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["match"] = new Dictionary<string, object?> { ["status_code"] = new[] { 2 } },
                        ["routes"] = new[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["handle"] = new[]
                                {
                                    new Dictionary<string, object?>
                                    {
                                        ["handler"] = "headers",
                                        ["request"] = new Dictionary<string, object?>
                                        {
                                            ["set"] = new Dictionary<string, object?>
                                            {
                                                ["X-Matcad-User"] = new[] { "{http.reverse_proxy.header.X-Matcad-User}" }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            };
        }
    }

    private object? BuildTls(List<RouteConfig> routes, MatcadSettings settings)
    {
        var policies = new List<object>();

        foreach (var route in routes.Where(r => r.Wildcard && r.ProviderId is > 0))
        {
            var provider = _store.Providers.FirstOrDefault(p => p.Id == route.ProviderId);
            if (provider == null || string.IsNullOrWhiteSpace(provider.Type)) continue;

            // The Type IS the Caddy DNS module name, so any compiled-in module
            // (including custom ones not in the registry) works.
            // A credential called "name" (only a hand-edited file or an old backup can have one) must not replace the module.
            var dnsProvider = new Dictionary<string, object?>();
            foreach (var kv in provider.Credentials.Where(kv => !kv.Key.Equals(ProviderRules.ModuleKey, StringComparison.OrdinalIgnoreCase)))
                dnsProvider[kv.Key] = kv.Value;
            dnsProvider[ProviderRules.ModuleKey] = provider.Type;

            var dns = new Dictionary<string, object?> { ["provider"] = dnsProvider };
            // Tuning for slow DNS providers (e.g. netcup): wait before validating,
            // and relax or disable Caddy's own propagation self-check.
            if (settings.AcmePropagationDelaySeconds > 0)
                dns["propagation_delay"] = $"{settings.AcmePropagationDelaySeconds}s";
            if (settings.AcmePropagationTimeoutSeconds < 0)
                dns["propagation_timeout"] = -1;                 // skip the propagation check
            else if (settings.AcmePropagationTimeoutSeconds > 0)
                dns["propagation_timeout"] = $"{settings.AcmePropagationTimeoutSeconds}s";

            var issuer = new Dictionary<string, object?>
            {
                ["module"] = "acme",
                ["challenges"] = new Dictionary<string, object?> { ["dns"] = dns }
            };
            // Per-domain email overrides the global one; fall back to the global
            // setting when the route doesn't specify its own.
            var acmeEmail = (route.AcmeEmail ?? "").Trim();
            if (acmeEmail.Length == 0) acmeEmail = settings.AcmeEmail?.Trim() ?? "";
            // Let's Encrypt rejects contact emails on the example.com domain
            // ("forbidden domain"), which would fail account registration and
            // block ALL certificate issuance. Drop such placeholder emails so
            // Caddy registers without a contact instead of breaking everything.
            if (acmeEmail.Length > 0 &&
                !acmeEmail.EndsWith("@example.com", StringComparison.OrdinalIgnoreCase) &&
                !acmeEmail.EndsWith(".example.com", StringComparison.OrdinalIgnoreCase))
                issuer["email"] = acmeEmail;

            // One wildcard cert per domain. Caddy subsumes concrete subdomains
            // (app.example.com, ...) under the *.example.com subject, so they are
            // served from this single cert instead of getting their own.
            // The apex (example.com) is only added when a route actually needs it.
            var apexHost = route.Host.StartsWith("*.") ? route.Host[2..] : route.Host;
            var subjects = new List<string> { route.Host };
            if (routes.Any(x => string.Equals(x.Host, apexHost, StringComparison.OrdinalIgnoreCase)))
                subjects.Add(apexHost);

            policies.Add(new Dictionary<string, object?>
            {
                ["subjects"] = subjects,
                ["issuers"] = new[] { issuer }
            });
        }

        if (policies.Count == 0) return null;
        return new Dictionary<string, object?>
        {
            ["automation"] = new Dictionary<string, object?> { ["policies"] = policies }
        };
    }

    /// <summary>The Location of a redirect. Caddy replaces {placeholders} in it, so braces are sent percent-encoded: only a trailing
    /// {http.request.uri} stays a placeholder: what <paramref name="keepPath"/> adds (path and query of the request), and what older
    /// versions of the Caddyfile import stored for <c>redir https://target{uri}</c>.</summary>
    public static string RedirectLocation(string url, bool keepPath = false)
    {
        const string KeepUri = "{http.request.uri}";
        var keep = keepPath || url.EndsWith(KeepUri, StringComparison.Ordinal);
        var target = (url.EndsWith(KeepUri, StringComparison.Ordinal) ? url[..^KeepUri.Length] : url).Replace("{", "%7B").Replace("}", "%7D");
        // The request URI starts with a slash: the target must not end with one, or the path would start with two.
        return keep ? target.TrimEnd('/') + KeepUri : target;
    }

    /// <summary>Turns "http://host:8080" or "host:8080" into a Caddy dial + https flag.</summary>
    private static (string Dial, bool IsHttps) ParseDial(string upstream)
    {
        upstream = upstream.Trim();
        if (upstream.Contains("://"))
        {
            if (Uri.TryCreate(upstream, UriKind.Absolute, out var uri))
            {
                var isHttps = uri.Scheme == "https";
                var port = uri.Port > 0 ? uri.Port : (isHttps ? 443 : 80);
                return ($"{uri.Host}:{port}", isHttps);
            }
        }
        // Assume host:port; default to :80 if no port given.
        return (upstream.Contains(':') ? upstream : $"{upstream}:80", false);
    }
}
