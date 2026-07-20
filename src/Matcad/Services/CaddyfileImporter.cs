using System.Text.Json.Nodes;
using Matcad.Config;

namespace Matcad.Services;

/// <summary>
/// Maps Caddy JSON (produced by <see cref="CaddyfileAdapter"/>) into Matcad's
/// model. Understands the common building blocks - a site block's host list,
/// named host matchers with per-host <c>handle</c> blocks (each becomes its own
/// route; specific hosts win over the catch-all), <c>reverse_proxy</c>, redirects,
/// <c>http_basic</c> and ACME DNS providers. Anything it can't represent is kept
/// as a raw-passthrough remainder so nothing is silently lost.
/// </summary>
public class CaddyfileImporter
{
    public class ImportRoute
    {
        public RouteConfig Route { get; init; } = new();
        public List<BasicAuthUser> BasicUsers { get; init; } = new();
        public string? ProviderName { get; set; }
    }

    public record Plan(
        List<ImportRoute> Routes,
        List<ProviderConfig> Providers,
        string RawRemainder,
        List<string> Notes);

    public Plan Analyze(string adaptedJson)
    {
        var notes = new List<string>();
        var routes = new List<ImportRoute>();
        var providers = new List<ProviderConfig>();
        var rawRoutes = new JsonArray();

        JsonObject? root;
        try { root = JsonNode.Parse(adaptedJson) as JsonObject; }
        catch { return new Plan(routes, providers, "", new() { "Adapted config was not valid JSON." }); }
        if (root == null) return new Plan(routes, providers, "", notes);

        // --- Providers from TLS automation (ACME DNS) ---
        var wildcardProvider = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // subject -> provider type
        var policies = root["apps"]?["tls"]?["automation"]?["policies"] as JsonArray;
        if (policies != null)
        {
            foreach (var pol in policies.OfType<JsonObject>())
            {
                var dns = pol["issuers"]?.AsArray().OfType<JsonObject>()
                    .Select(i => i["challenges"]?["dns"]?["provider"] as JsonObject)
                    .FirstOrDefault(p => p != null);
                if (dns == null) continue;
                var name = dns["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name)) continue;
                var creds = new Dictionary<string, string>();
                foreach (var (k, v) in dns)
                    if (k != "name" && v != null) creds[k] = v.ToString();
                if (!providers.Any(p => p.Type == name))
                    providers.Add(new ProviderConfig { Name = $"Imported {name}", Type = name!, Credentials = creds });
                foreach (var s in pol["subjects"]?.AsArray().Select(x => x?.GetValue<string>()) ?? Enumerable.Empty<string?>())
                    if (!string.IsNullOrEmpty(s)) wildcardProvider[s!] = name!;
            }
        }

        // --- HTTP routes ---
        var servers = root["apps"]?["http"]?["servers"] as JsonObject;
        if (servers != null)
        {
            foreach (var srv in servers.Select(kv => kv.Value as JsonObject).Where(s => s != null))
            {
                var srvRoutes = srv!["routes"] as JsonArray;
                if (srvRoutes == null) continue;
                foreach (var route in srvRoutes.OfType<JsonObject>())
                {
                    if (!TryMapTopRoute(route, wildcardProvider, routes))
                        rawRoutes.Add(route.DeepClone());
                }
            }
        }

        var rawRemainder = "";
        if (rawRoutes.Count > 0)
        {
            var remainder = new JsonObject
            {
                ["apps"] = new JsonObject
                {
                    ["http"] = new JsonObject
                    {
                        ["servers"] = new JsonObject { ["matcad"] = new JsonObject { ["routes"] = rawRoutes } }
                    }
                }
            };
            rawRemainder = remainder.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            notes.Add($"{rawRoutes.Count} route block(s) could not be fully mapped and are kept as raw passthrough.");
        }

        notes.Insert(0, $"Recognized {routes.Count} route(s), " +
            $"{routes.Count(r => r.BasicUsers.Count > 0)} with basic-auth, {providers.Count} DNS provider(s).");
        return new Plan(routes, providers, rawRemainder, notes);
    }

    /// <summary>Maps one top-level route (a site block) into Matcad routes.
    /// Returns false if the block can't be fully represented (caller keeps it raw).</summary>
    private static bool TryMapTopRoute(JsonObject route, Dictionary<string, string> wildcardProvider, List<ImportRoute> outRoutes)
    {
        if (!TryHostMatch(route["match"] as JsonArray, out var scope) || scope is not { Count: > 0 })
            return false; // no clean host scope -> raw

        // Expand the block into segments: (specificHosts?, collected-handlers).
        var segments = new List<(List<string>? Hosts, Collected C)>();
        var handle = route["handle"] as JsonArray;
        if (handle == null) return false;

        var sub = handle.OfType<JsonObject>().FirstOrDefault(h => h["handler"]?.GetValue<string>() == "subroute");
        if (sub != null)
        {
            // Any non-subroute sibling handler we don't understand -> keep raw.
            foreach (var h in handle.OfType<JsonObject>())
                if (h["handler"]?.GetValue<string>() is not "subroute") return false;

            foreach (var inner in (sub["routes"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            {
                if (!TryHostMatch(inner["match"] as JsonArray, out var innerHosts)) return false;
                var c = new Collected();
                if (!TryCollect(inner["handle"] as JsonArray, c)) return false;
                if (c.Upstream == null && c.Redirect == null && c.BasicUsers.Count == 0) continue;
                segments.Add((innerHosts, c));
            }
        }
        else
        {
            var c = new Collected();
            if (!TryCollect(handle, c)) return false;
            segments.Add((null, c));
        }

        if (segments.Count == 0) return false;

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Specific host matchers win; then the catch-all covers the rest of the scope.
        foreach (var seg in segments.Where(s => s.Hosts != null))
            foreach (var host in seg.Hosts!)
                if (claimed.Add(host)) Emit(host, seg.C, wildcardProvider, outRoutes);
        foreach (var seg in segments.Where(s => s.Hosts == null))
            foreach (var host in scope)
                if (claimed.Add(host)) Emit(host, seg.C, wildcardProvider, outRoutes);
        return true;
    }

    private static void Emit(string host, Collected c, Dictionary<string, string> wildcardProvider, List<ImportRoute> outRoutes)
    {
        if (c.Upstream == null && c.Redirect == null) return; // nothing to proxy to
        var r = new RouteConfig
        {
            Host = host,
            Name = host,
            Wildcard = host.StartsWith("*."),
            Upstream = c.Upstream,
            FallbackUrl = c.Upstream == null ? c.Redirect : null,
            Enabled = true
        };
        var ir = new ImportRoute { Route = r, BasicUsers = new List<BasicAuthUser>(c.BasicUsers) };
        if (r.Wildcard && wildcardProvider.TryGetValue(host, out var pn)) ir.ProviderName = pn;
        outRoutes.Add(ir);
    }

    private sealed class Collected
    {
        public string? Upstream;
        public string? Redirect;
        public List<BasicAuthUser> BasicUsers = new();
    }

    /// <summary>Parses a match array. Returns false if it contains anything other
    /// than a single host matcher (path/header/etc. can't be modelled). hosts is
    /// null for "no matcher" (catch-all).</summary>
    private static bool TryHostMatch(JsonArray? match, out List<string>? hosts)
    {
        hosts = null;
        if (match == null || match.Count == 0) return true; // catch-all
        var result = new List<string>();
        foreach (var m in match.OfType<JsonObject>())
        {
            foreach (var (key, _) in m)
                if (key != "host") return false; // non-host matcher -> unmappable
            foreach (var h in m["host"]?.AsArray().Select(x => x?.GetValue<string>()) ?? Enumerable.Empty<string?>())
                if (!string.IsNullOrEmpty(h)) result.Add(h!);
        }
        hosts = result.Count > 0 ? result : null;
        return true;
    }

    /// <summary>Collects reverse_proxy / redirect / http_basic from a handler array
    /// (descending into subroutes). Returns false on an unknown handler type.</summary>
    private static bool TryCollect(JsonArray? handlers, Collected c)
    {
        if (handlers == null) return true;
        foreach (var h in handlers.OfType<JsonObject>())
        {
            switch (h["handler"]?.GetValue<string>())
            {
                case "subroute":
                    foreach (var ir in (h["routes"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                        if (!TryCollect(ir["handle"] as JsonArray, c)) return false;
                    break;
                case "reverse_proxy":
                    // A reverse_proxy with handle_response is a forward_auth-style
                    // verifier (Authelia/Authentik/Matcad). We can't model that, and
                    // treating it as the upstream would silently strip the auth, so
                    // keep the whole block as raw passthrough instead.
                    if (h["handle_response"] != null) return false;
                    var dial = (h["upstreams"] as JsonArray)?.OfType<JsonObject>()
                        .Select(u => u["dial"]?.GetValue<string>()).FirstOrDefault(d => !string.IsNullOrEmpty(d));
                    if (dial != null)
                    {
                        var https = h["transport"]?["tls"] != null;
                        c.Upstream = (https ? "https://" : "http://") + dial;
                    }
                    break;
                case "static_response":
                    var loc = (h["headers"]?["Location"] as JsonArray)?.FirstOrDefault()?.GetValue<string>();
                    if (!string.IsNullOrEmpty(loc)) c.Redirect = loc;
                    break;
                case "authentication":
                    var accounts = h["providers"]?["http_basic"]?["accounts"] as JsonArray;
                    if (accounts != null)
                        foreach (var a in accounts.OfType<JsonObject>())
                        {
                            var user = a["username"]?.GetValue<string>();
                            var pw = a["password"]?.GetValue<string>();
                            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pw)) continue;
                            try { c.BasicUsers.Add(new BasicAuthUser { Username = user!, PasswordHash = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(pw!)) }); }
                            catch { return false; }
                        }
                    break;
                case null:
                case "":
                    break;
                default:
                    return false; // unknown handler -> keep the whole block as raw
            }
        }
        return true;
    }
}
