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
///                   optional auth handler + reverse_proxy / fallback redirect)
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
        var routes = _routes.All().Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Host)).ToList();

        // Specific hosts must be matched before wildcards.
        var ordered = routes.OrderBy(r => r.Wildcard ? 1 : 0).ThenBy(r => r.Host).ToList();

        var httpRoutes = ordered.Select(BuildRoute).ToList();

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
                        ["encoder"] = new Dictionary<string, object?> { ["format"] = "json" },
                        ["include"] = new[] { "http.log.access.matcad" }
                    }
                }
            },
            ["apps"] = new Dictionary<string, object?>
            {
                ["http"] = new Dictionary<string, object?>
                {
                    ["servers"] = new Dictionary<string, object?>
                    {
                        ["matcad"] = new Dictionary<string, object?>
                        {
                            ["listen"] = new[] { ":80", ":443" },
                            ["routes"] = httpRoutes,
                            // Route this server's access logs to the "matcad"
                            // logger so the custom file log below captures them.
                            ["logs"] = new Dictionary<string, object?>
                            {
                                ["default_logger_name"] = "matcad"
                            }
                        }
                    }
                }
            }
        };

        var tls = BuildTls(routes, settings);
        if (tls != null)
            ((Dictionary<string, object?>)config["apps"]!)["tls"] = tls;

        return config;
    }

    private Dictionary<string, object?> BuildRoute(RouteConfig route)
    {
        var handlers = new List<object>();

        var auth = route.AuthenticationId is > 0
            ? _store.Authentications.FirstOrDefault(a => a.Id == route.AuthenticationId)
            : null;
        if (auth != null)
            handlers.AddRange(BuildAuthHandlers(auth));

        handlers.Add(BuildTerminalHandler(route));

        return new Dictionary<string, object?>
        {
            ["match"] = new[] { new Dictionary<string, object?> { ["host"] = new[] { route.Host } } },
            ["handle"] = handlers,
            ["terminal"] = true
        };
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
            if (isHttps)
                proxy["transport"] = new Dictionary<string, object?>
                {
                    ["protocol"] = "http",
                    ["tls"] = new Dictionary<string, object?>()
                };
            return proxy;
        }

        if (!string.IsNullOrWhiteSpace(route.FallbackUrl))
        {
            return new Dictionary<string, object?>
            {
                ["handler"] = "static_response",
                ["status_code"] = 302,
                ["headers"] = new Dictionary<string, object?>
                {
                    ["Location"] = new[] { route.FallbackUrl }
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
                            ["X-Forwarded-Host"] = new[] { "{http.request.host}" }
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
            var type = provider != null ? ProviderTypes.Find(provider.Type) : null;
            if (provider == null || type == null) continue;

            var dnsProvider = new Dictionary<string, object?> { ["name"] = type.CaddyModule };
            foreach (var kv in provider.Credentials)
                dnsProvider[kv.Key] = kv.Value;

            var issuer = new Dictionary<string, object?>
            {
                ["module"] = "acme",
                ["challenges"] = new Dictionary<string, object?>
                {
                    ["dns"] = new Dictionary<string, object?> { ["provider"] = dnsProvider }
                }
            };
            if (!string.IsNullOrWhiteSpace(settings.AcmeEmail))
                issuer["email"] = settings.AcmeEmail;

            // One wildcard cert per domain. Caddy subsumes concrete subdomains
            // (app.example.com, …) under the *.example.com subject, so they are
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
