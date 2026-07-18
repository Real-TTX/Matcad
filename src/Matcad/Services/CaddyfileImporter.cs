using System.Text.Json.Nodes;
using Matcad.Config;

namespace Matcad.Services;

/// <summary>
/// Maps Caddy JSON (produced by <see cref="CaddyfileAdapter"/>) into Matcad's
/// model. Recognizes the common building blocks — host matcher, reverse_proxy,
/// redirect, http_basic and ACME DNS providers. Anything it can't represent is
/// collected into a raw-passthrough remainder so nothing is silently lost.
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
        var wildcardProvider = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // subject -> provider name
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
                    var hosts = route["match"]?.AsArray()
                        .OfType<JsonObject>()
                        .SelectMany(m => m["host"]?.AsArray().Select(h => h?.GetValue<string>()) ?? Enumerable.Empty<string?>())
                        .Where(h => !string.IsNullOrEmpty(h)).Select(h => h!).Distinct().ToList()
                        ?? new List<string>();

                    var collected = new Collected();
                    var ok = TryCollect(route["handle"] as JsonArray, collected);

                    // Mappable only if: has host(s), a terminal we understand, and nothing unknown.
                    if (!ok || hosts.Count == 0 || (collected.Upstream == null && collected.Redirect == null))
                    {
                        rawRoutes.Add(route.DeepClone());
                        continue;
                    }

                    foreach (var host in hosts)
                    {
                        var r = new RouteConfig
                        {
                            Host = host,
                            Name = host,
                            Wildcard = host.StartsWith("*."),
                            Upstream = collected.Upstream,
                            FallbackUrl = collected.Upstream == null ? collected.Redirect : null,
                            Enabled = true
                        };
                        var ir = new ImportRoute { Route = r, BasicUsers = collected.BasicUsers };
                        if (r.Wildcard && wildcardProvider.TryGetValue(host, out var pn)) ir.ProviderName = pn;
                        routes.Add(ir);
                    }
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
                        ["servers"] = new JsonObject
                        {
                            ["matcad"] = new JsonObject { ["routes"] = rawRoutes }
                        }
                    }
                }
            };
            rawRemainder = remainder.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            notes.Add($"{rawRoutes.Count} route(s) could not be mapped and are kept as raw passthrough.");
        }

        notes.Insert(0, $"Recognized {routes.Count} route(s), " +
            $"{routes.Count(r => r.BasicUsers.Count > 0)} with basic-auth, {providers.Count} DNS provider(s).");
        return new Plan(routes, providers, rawRemainder, notes);
    }

    private sealed class Collected
    {
        public string? Upstream;
        public string? Redirect;
        public List<BasicAuthUser> BasicUsers = new();
    }

    /// <summary>Walks a handler array (descending into subroutes). Returns false
    /// if it hits a handler type Matcad can't represent.</summary>
    private static bool TryCollect(JsonArray? handlers, Collected c)
    {
        if (handlers == null) return true;
        foreach (var h in handlers.OfType<JsonObject>())
        {
            switch (h["handler"]?.GetValue<string>())
            {
                case "subroute":
                    var inner = h["routes"] as JsonArray;
                    if (inner != null)
                        foreach (var ir in inner.OfType<JsonObject>())
                            if (!TryCollect(ir["handle"] as JsonArray, c)) return false;
                    break;
                case "reverse_proxy":
                    var dial = (h["upstreams"] as JsonArray)?.OfType<JsonObject>()
                        .Select(u => u["dial"]?.GetValue<string>()).FirstOrDefault(d => !string.IsNullOrEmpty(d));
                    if (dial != null)
                    {
                        var https = h["transport"]?["protocol"]?.GetValue<string>() == "http"
                                    && h["transport"]?["tls"] != null;
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
                    return false; // unknown handler -> keep whole route as raw
            }
        }
        return true;
    }
}
