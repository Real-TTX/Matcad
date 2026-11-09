using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Matcad.Config;

/// <summary>
/// What a binding of <c>web.route</c> asks for: the consumer (its id and name), the host it wants or the port it wants (as the parameters came, a text)
/// with the name or IP address the browser reaches this machine under, its address on the shared network and who manages it.
/// </summary>
public sealed record RouteBindInput(string Consumer, string Name, string? Host, string? Upstream, string ManagedBy, string? Port = null, string? PublicHost = null);

/// <summary>Why a binding was refused: the field (<c>host</c>, <c>port</c>, <c>publicHost</c>, <c>internalUrl</c>, empty for the route as a whole), a message
/// for a person, and whether the host or port belongs to another route (a conflict) rather than the input being wrong.</summary>
public sealed record RouteBindRefusal(string Field, string Message, bool Conflict = false);

/// <summary>
/// The routes that are bindings of the capability <c>web.route</c> (MatDesign docs/PLATFORM.md): a binding is a route to the consumer's internal address,
/// named after the consumer, either from <c>host</c> (a name of the domain) or from a <c>port</c> of its own on Caddy (a server per port, for a Mattiverse
/// without a domain: <c>http://&lt;machine&gt;:&lt;port&gt;</c>). One route per consumer, marked with <see cref="RouteConfig.Binding"/>; a new host or port
/// moves it, also from a host to a port and back. The route is built and checked by <see cref="RouteEditor"/> and saved by
/// <see cref="ConfigStore.TryUpsertRoute"/>, the same way the REST API saves a route, and everything else of an existing route (authentication, guards,
/// options) stays as an administrator set it.
/// A route of the same host or port without a binding is taken over only when the same tool manages it (Mattiverse made the routes of its apps through
/// <c>/api/v1</c> before the bindings); a route made by hand or by another tool is never taken over: the binding is refused as a conflict.
/// </summary>
public static class RouteBindings
{
    /// <summary>The <c>managedBy</c> of a binding made by hand (the Connections area). Such a route has no <see cref="ConfigEntity.ManagedBy"/>.</summary>
    public const string ByHand = "manual";

    /// <summary>The lowest port a binding may ask for: below it are the well-known ports of the system (and Caddy's 80 and 443).</summary>
    public const int LowestPort = 1024;

    /// <summary>The ports Caddy itself listens on: the main server (80, 443) and its admin API (2019).</summary>
    public static readonly IReadOnlyList<int> CaddyPorts = [80, 443, 2019];

    // A binding reads the routes, decides and saves: one at a time, so two calls for one consumer cannot make two routes.
    private static readonly Lock Gate = new();

    /// <summary>The public address of a host: plain http for a development name under <c>.localhost</c>, which cannot get a public certificate.</summary>
    public static string PublicUrlOf(string host)
        => host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ? $"http://{host}" : $"https://{host}";

    /// <summary>
    /// The public address of the route of a binding: <see cref="PublicUrlOf(string)"/> of its host, or <c>http://&lt;publicHost&gt;:&lt;port&gt;</c> for a port
    /// route (an IPv6 address in brackets; a port server is plain HTTP). Null for a port route whose broker named no machine: Matcad does not know the name the
    /// browser uses, and <c>localhost</c> would be wrong for everyone but a browser on this machine.
    /// </summary>
    public static string? PublicUrlOf(RouteConfig route)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (route.ListenPort is not > 0)
            return string.IsNullOrWhiteSpace(route.Host) ? null : PublicUrlOf(route.Host);
        if (route.Binding?.PublicHost is not { Length: > 0 } machine)
            return null;
        var authority = machine.Contains(':', StringComparison.Ordinal) ? $"[{machine}]" : machine;
        return string.Create(CultureInfo.InvariantCulture, $"http://{authority}:{route.ListenPort}");
    }

    /// <summary>Who manages the binding of a route: its <c>managedBy</c>, or <see cref="ByHand"/> when nobody does.</summary>
    public static string ManagedByOf(RouteConfig route) => string.IsNullOrWhiteSpace(route.ManagedBy) ? ByHand : route.ManagedBy;

    /// <summary>The routes that are bindings, by consumer.</summary>
    public static IReadOnlyList<RouteConfig> List(ConfigStore store)
        => [.. store.Routes.Where(r => r.Binding is { Consumer.Length: > 0 }).OrderBy(r => r.Binding!.Consumer, StringComparer.Ordinal)];

    /// <summary>The route of <paramref name="consumer"/>, or null.</summary>
    public static RouteConfig? Find(ConfigStore store, string consumer)
        => store.Routes.FirstOrDefault(r => IsOf(r, consumer));

    /// <summary>
    /// Makes or moves the route of the consumer. Returns the saved route, or why nothing was saved. The same input twice is one route; Caddy is not
    /// told here (the caller pushes the config).
    /// </summary>
    public static (RouteConfig? Route, RouteBindRefusal? Refusal) Bind(ConfigStore store, RouteBindInput input, string language, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(input);
        string T(string de, string en) => language == "de" ? de : en;

        var host = (input.Host ?? "").Trim().ToLowerInvariant();
        var portText = (input.Port ?? "").Trim();
        var upstream = (input.Upstream ?? "").Trim().TrimEnd('/');
        if (host.Length == 0 && portText.Length == 0 && upstream.Length == 0)
        {
            // Nothing to change for a consumer that has its route ("New key" of the Connections area sends a name only): its values as they are.
            if (Find(store, input.Consumer) is { } bound)
                return (bound, null);

            // What the generic "Connect an app by hand" sends: a name only. A route without a host or port and a target would serve nothing.
            return Refused("", T(
                "Eine Route braucht den Namen, unter dem die App erreichbar sein soll (host), oder einen eigenen Port (port), und die Adresse der App im Netz. Mattiverse schickt beides; ohne Mattiverse lege die Route unter Routen an.",
                "A route needs the name the app is reached under (host) or a port of its own (port), and the app's address on the network. Mattiverse sends both; without it, add the route on the Routes page."));
        }

        // A route is reached under a name of the domain or on a port of its own, never both: one route per consumer.
        if (host.Length > 0 && portText.Length > 0)
            return Refused("port", T("Schicke host oder port, nicht beides: die Route einer App ist unter einem Namen der Domain oder auf einem eigenen Port erreichbar.",
                "Send host or port, not both: the route of an app is reached under a name of the domain or on a port of its own."));

        int? port = null;
        string? publicHost = null;
        if (portText.Length > 0)
        {
            var (number, portError) = ParsePort(portText, language);
            if (portError != null) return Refused("port", portError);
            port = number;
            // The machine is named only for the value of the binding: without it the binding holds, it just has no public address to tell.
            var machine = (input.PublicHost ?? "").Trim();
            if (machine.Length > 0)
            {
                publicHost = NormalizePublicHost(machine);
                if (publicHost is null)
                    return Refused("publicHost", T(
                        "publicHost muss der Name oder die IP-Adresse dieses Rechners sein, wie nas.local, 192.168.1.20 oder localhost: kein http://, Pfad oder Port.",
                        "publicHost must be the name or IP address of this machine, like nas.local, 192.168.1.20 or localhost: no http://, path or port."));
            }
        }
        else if (host.Length == 0)
        {
            return Refused("host", T("Gib den Namen an, unter dem die App erreichbar sein soll (host), etwa matdrive.example.com, oder einen eigenen Port (port).",
                "Send the name the app is reached under (host), like matdrive.example.com, or a port of its own (port)."));
        }
        else if (!IsDomainName(host))
        {
            return Refused("host", T("Der Host muss ein Name einer Domain sein wie matdrive.example.com: kein http://, Pfad, Port, Platzhalter (*) oder IP-Adresse.",
                "The host must be a name of a domain like matdrive.example.com: no http://, path, port, wildcard (*) or IP address."));
        }

        if (upstream.Length == 0)
            return Refused("internalUrl", T("Die Adresse der App im Netz fehlt (consumer.internalUrl): wohin die Route führt.",
                "The app's address on the network is missing (consumer.internalUrl): where the route leads."));
        if (!IsInternalUrl(upstream))
            return Refused("internalUrl", T("Die Adresse der App im Netz muss wie http://container:8080 aussehen (http oder https, ohne Pfad).",
                "The app's address on the network must look like http://container:8080 (http or https, no path)."));

        // What the messages call the target (in a sentence and at its start) and the field they name.
        var field = port is null ? "host" : "port";
        var target = port is null ? host : T($"Port {port}", $"port {port}");
        var targetStart = port is null ? host : $"Port {port}";
        lock (Gate)
        {
            var own = Find(store, input.Consumer);
            var holder = store.Routes.FirstOrDefault(r => r.Id != own?.Id && (port is null
                ? r.ListenPort is not > 0 && string.Equals(r.Host, host, StringComparison.OrdinalIgnoreCase)
                : r.ListenPort == port));
            var current = own;
            if (holder != null)
            {
                if (holder.Binding is { Consumer.Length: > 0 } other)
                    return Refused(field, T($"{targetStart} ist schon die Route von {other.Consumer}.", $"{targetStart} is the route of {other.Consumer} already."), conflict: true);
                if (!SameManager(holder, input.ManagedBy))
                {
                    var who = string.IsNullOrWhiteSpace(holder.ManagedBy)
                        ? T("von Hand angelegt", "made by hand")
                        : T($"verwaltet von {holder.ManagedBy}", $"managed by {holder.ManagedBy}");
                    return Refused(field, T(
                        $"Für {target} gibt es schon die Route „{holder.Name}“ ({who}). Matcad übernimmt sie nicht: lösche sie unter Routen oder wähle {(port is null ? "einen anderen Host" : "einen anderen Port")}.",
                        $"{targetStart} has the route \"{holder.Name}\" already ({who}). Matcad does not take it over: delete it on the Routes page or choose another {field}."), conflict: true);
                }

                // The same tool made this route before it bound its apps (Mattiverse through /api/v1): it becomes the binding.
                current = holder;
            }

            // A port server has no paths of its own (RouteEditor refuses locations there): what an administrator set up is not dropped without a word.
            if (port is not null && current is { Locations.Count: > 0 })
                return Refused("port", T(
                    $"Die Route „{current.Name}“ hat eigene Pfade (Locations), die nur mit einem Host gehen. Entferne sie unter Routen, dann bekommt die App ihren Port.",
                    $"The route \"{current.Name}\" has locations (paths of their own), which only work with a host. Remove them on the Routes page, then the app gets its port."), conflict: true);

            var route = new RouteConfig { Id = current?.Id ?? 0 };
            var name = string.IsNullOrWhiteSpace(input.Name) ? input.Consumer : input.Name.Trim();
            // Host or port, target and name are the binding's; the rest stays as the route has it (an administrator may have added an authentication or a guard).
            var form = new RouteForm(port is null ? host : null, Wildcard: false, Redirect: false, upstream, current?.InsecureSkipVerify ?? false,
                FallbackUrl: null, RedirectPermanent: false, current?.AuthenticationId, ProviderId: null, AcmeEmail: null,
                Enabled: current?.Enabled ?? true, name, current?.AllowEmbedding ?? false, ListenPort: port,
                current?.UpstreamHost, current?.UpstreamHeaders, current?.RequireHeader, current?.AllowedIps,
                current is null ? null : RouteEditor.OptionsOf(current));
            var error = RouteEditor.Apply(form, route);
            if (error != null) return Refused("", error);

            route.ManagedBy = input.ManagedBy == ByHand ? null : input.ManagedBy;
            route.Binding = new RouteBinding { Consumer = input.Consumer, Since = own?.Binding?.Since ?? now, PublicHost = publicHost };
            if (!store.TryUpsertRoute(route, null, updateSameTarget: false))
                return Refused(field, T($"Eine andere Route nutzt {target} schon.", $"Another route uses {target} already."), conflict: true);

            // The consumer moved onto a route it took over: its old one goes, so it keeps one route.
            if (own != null && own.Id != route.Id)
                store.DeleteRoute(own.Id);
            return (route, null);
        }
    }

    /// <summary>Removes the route of <paramref name="consumer"/> when <paramref name="managedBy"/> manages it (null: whoever does). False when there is none.</summary>
    public static bool Unbind(ConfigStore store, string consumer, string? managedBy)
    {
        ArgumentNullException.ThrowIfNull(store);
        lock (Gate)
        {
            var routes = store.Routes.Where(r => IsOf(r, consumer)
                                                 && (managedBy is null || string.Equals(ManagedByOf(r), managedBy.Trim(), StringComparison.OrdinalIgnoreCase)))
                .ToList();
            foreach (var route in routes)
                store.DeleteRoute(route.Id);
            return routes.Count > 0;
        }
    }

    /// <summary>A name of a domain: at least two labels, no wildcard, no IP address, no scheme, path or port.</summary>
    public static bool IsDomainName(string host)
        => host.Length <= 253 && host.Contains('.') && !host.StartsWith("*.", StringComparison.Ordinal)
           && RouteEditor.IsHostName(host) && !IPAddress.TryParse(host, out _);

    /// <summary>An http(s) address without a path, query or user: Caddy dials host and port only, so a path would be lost without a word.</summary>
    public static bool IsInternalUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0
           && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0
           && RouteEditor.IsUpstream(url);

    /// <summary>
    /// The name or IP address of this machine as the browser uses it (<c>nas.local</c>, <c>192.168.1.20</c>, <c>localhost</c>, <c>fe80::1</c>), small and an IPv6
    /// address without brackets; null when it is not one: a scheme, path, port, a zone (<c>%eth0</c>) or a short IPv4 such as <c>10.1</c> is refused.
    /// </summary>
    public static string? NormalizePublicHost(string? value)
    {
        var text = (value ?? "").Trim().ToLowerInvariant();
        // An IPv6 address may come in the brackets of a URL; inside them it must be all there is.
        if (text.StartsWith('[') && text.EndsWith(']'))
            text = text[1..^1];
        if (text.Length is 0 or > 253)
            return null;

        // IPv6: hex digits, colons and the dots of an embedded IPv4 only. IPAddress.TryParse alone would take "[::1]:8080" and drop the port.
        if (text.Contains(':', StringComparison.Ordinal))
            return text.All(c => char.IsAsciiHexDigit(c) || c is ':' or '.')
                   && IPAddress.TryParse(text, out var v6) && v6.AddressFamily == AddressFamily.InterNetworkV6
                ? v6.ToString() : null;

        // Digits and dots only is an IPv4 address, written as it is meant (IPAddress.TryParse would take "10.1" as 10.0.0.1 and "010" as octal).
        if (text.All(c => char.IsAsciiDigit(c) || c == '.'))
            return IPAddress.TryParse(text, out var v4) && v4.AddressFamily == AddressFamily.InterNetwork && v4.ToString() == text ? text : null;

        return RouteEditor.IsHostName(text) ? text : null;
    }

    // 1024 to 65535 and none of Caddy's own; a text from the parameters, digits only.
    private static (int Port, string? Error) ParsePort(string text, string language)
    {
        string T(string de, string en) => language == "de" ? de : en;
        var parsed = int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port);
        if (parsed && CaddyPorts.Contains(port))
            return (0, T("Die Ports 80, 443 und 2019 nutzt Caddy selbst.", "Ports 80, 443 and 2019 are used by Caddy itself."));
        if (!parsed || port is < LowestPort or > 65535)
            return (0, T($"Der Port muss eine Zahl von {LowestPort} bis 65535 sein.", $"The port must be a number from {LowestPort} to 65535."));
        return (port, null);
    }

    private static bool IsOf(RouteConfig route, string consumer)
        => string.Equals(route.Binding?.Consumer, consumer, StringComparison.OrdinalIgnoreCase);

    // Only a tool takes over its own route: a route without managedBy was made by hand, and a binding by hand never takes one over.
    private static bool SameManager(RouteConfig route, string managedBy)
        => !string.IsNullOrWhiteSpace(route.ManagedBy) && managedBy != ByHand
           && string.Equals(route.ManagedBy.Trim(), managedBy, StringComparison.OrdinalIgnoreCase);

    private static (RouteConfig?, RouteBindRefusal?) Refused(string field, string message, bool conflict = false)
        => (null, new RouteBindRefusal(field, message, conflict));
}
