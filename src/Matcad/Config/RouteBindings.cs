using System.Net;

namespace Matcad.Config;

/// <summary>What a binding of <c>web.route</c> asks for: the consumer (its id and name), the host it wants, its address on the shared network and who manages it.</summary>
public sealed record RouteBindInput(string Consumer, string Name, string? Host, string? Upstream, string ManagedBy);

/// <summary>Why a binding was refused: the field (<c>host</c>, <c>internalUrl</c>, empty for the route as a whole), a message for a person, and whether
/// the host belongs to another route (a conflict) rather than the input being wrong.</summary>
public sealed record RouteBindRefusal(string Field, string Message, bool Conflict = false);

/// <summary>
/// The routes that are bindings of the capability <c>web.route</c> (MatDesign docs/PLATFORM.md): a binding is a route from <c>host</c> to the consumer's
/// internal address, named after the consumer. One route per consumer, marked with <see cref="RouteConfig.Binding"/>; a new host moves it. The route is
/// built and checked by <see cref="RouteEditor"/> and saved by <see cref="ConfigStore.TryUpsertRoute"/>, the same way the REST API saves a route, and
/// everything else of an existing route (authentication, guards, options) stays as an administrator set it.
/// A route of the same host without a binding is taken over only when the same tool manages it (Mattiverse made the routes of its apps through
/// <c>/api/v1</c> before the bindings); a route made by hand or by another tool is never taken over: the binding is refused as a conflict.
/// </summary>
public static class RouteBindings
{
    /// <summary>The <c>managedBy</c> of a binding made by hand (the Connections area). Such a route has no <see cref="ConfigEntity.ManagedBy"/>.</summary>
    public const string ByHand = "manual";

    // A binding reads the routes, decides and saves: one at a time, so two calls for one consumer cannot make two routes.
    private static readonly Lock Gate = new();

    /// <summary>The public address of a host: plain http for a development name under <c>.localhost</c>, which cannot get a public certificate.</summary>
    public static string PublicUrlOf(string host)
        => host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ? $"http://{host}" : $"https://{host}";

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
        var upstream = (input.Upstream ?? "").Trim().TrimEnd('/');
        if (host.Length == 0 && upstream.Length == 0)
        {
            // Nothing to change for a consumer that has its route ("New key" of the Connections area sends a name only): its values as they are.
            if (Find(store, input.Consumer) is { } bound)
                return (bound, null);


            // What the generic "Connect an app by hand" sends: a name only. A route without a host and a target would serve nothing.
            return Refused("", T(
                "Eine Route braucht den Namen, unter dem die App erreichbar sein soll (host), und die Adresse der App im Netz. Mattiverse schickt beides; ohne Mattiverse lege die Route unter Routen an.",
                "A route needs the name the app is reached under (host) and the app's address on the network. Mattiverse sends both; without it, add the route on the Routes page."));
        }

        if (host.Length == 0)
            return Refused("host", T("Gib den Namen an, unter dem die App erreichbar sein soll (host), etwa matdrive.example.com.",
                "Send the name the app is reached under (host), like matdrive.example.com."));
        if (!IsDomainName(host))
            return Refused("host", T("Der Host muss ein Name einer Domain sein wie matdrive.example.com: kein http://, Pfad, Port, Platzhalter (*) oder IP-Adresse.",
                "The host must be a name of a domain like matdrive.example.com: no http://, path, port, wildcard (*) or IP address."));
        if (upstream.Length == 0)
            return Refused("internalUrl", T("Die Adresse der App im Netz fehlt (consumer.internalUrl): wohin die Route führt.",
                "The app's address on the network is missing (consumer.internalUrl): where the route leads."));
        if (!IsInternalUrl(upstream))
            return Refused("internalUrl", T("Die Adresse der App im Netz muss wie http://container:8080 aussehen (http oder https, ohne Pfad).",
                "The app's address on the network must look like http://container:8080 (http or https, no path)."));

        lock (Gate)
        {
            var own = Find(store, input.Consumer);
            var holder = store.Routes.FirstOrDefault(r => r.ListenPort is not > 0 && r.Id != own?.Id
                                                          && string.Equals(r.Host, host, StringComparison.OrdinalIgnoreCase));
            var current = own;
            if (holder != null)
            {
                if (holder.Binding is { Consumer.Length: > 0 } other)
                    return Refused("host", T($"{host} ist schon die Route von {other.Consumer}.", $"{host} is the route of {other.Consumer} already."), conflict: true);
                if (!SameManager(holder, input.ManagedBy))
                {
                    var who = string.IsNullOrWhiteSpace(holder.ManagedBy)
                        ? T("von Hand angelegt", "made by hand")
                        : T($"verwaltet von {holder.ManagedBy}", $"managed by {holder.ManagedBy}");
                    return Refused("host", T(
                        $"Für {host} gibt es schon die Route „{holder.Name}“ ({who}). Matcad übernimmt sie nicht: lösche sie unter Routen oder wähle einen anderen Host.",
                        $"{host} has the route \"{holder.Name}\" already ({who}). Matcad does not take it over: delete it on the Routes page or choose another host."), conflict: true);
                }

                // The same tool made this route before it bound its apps (Mattiverse through /api/v1): it becomes the binding.
                current = holder;
            }

            var route = new RouteConfig { Id = current?.Id ?? 0 };
            var name = string.IsNullOrWhiteSpace(input.Name) ? input.Consumer : input.Name.Trim();
            // Host, target and name are the binding's; the rest stays as the route has it (an administrator may have added an authentication or a guard).
            var form = new RouteForm(host, Wildcard: false, Redirect: false, upstream, current?.InsecureSkipVerify ?? false,
                FallbackUrl: null, RedirectPermanent: false, current?.AuthenticationId, ProviderId: null, AcmeEmail: null,
                Enabled: current?.Enabled ?? true, name, current?.AllowEmbedding ?? false, ListenPort: null,
                current?.UpstreamHost, current?.UpstreamHeaders, current?.RequireHeader, current?.AllowedIps,
                current is null ? null : RouteEditor.OptionsOf(current));
            var error = RouteEditor.Apply(form, route);
            if (error != null) return Refused("", error);

            route.ManagedBy = input.ManagedBy == ByHand ? null : input.ManagedBy;
            route.Binding = new RouteBinding { Consumer = input.Consumer, Since = own?.Binding?.Since ?? now };
            if (!store.TryUpsertRoute(route, null, updateSameTarget: false))
                return Refused("host", T($"Eine andere Route nutzt {host} schon.", $"Another route uses {host} already."), conflict: true);

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

    private static bool IsOf(RouteConfig route, string consumer)
        => string.Equals(route.Binding?.Consumer, consumer, StringComparison.OrdinalIgnoreCase);

    // Only a tool takes over its own route: a route without managedBy was made by hand, and a binding by hand never takes one over.
    private static bool SameManager(RouteConfig route, string managedBy)
        => !string.IsNullOrWhiteSpace(route.ManagedBy) && managedBy != ByHand
           && string.Equals(route.ManagedBy.Trim(), managedBy, StringComparison.OrdinalIgnoreCase);

    private static (RouteConfig?, RouteBindRefusal?) Refused(string field, string message, bool conflict = false)
        => (null, new RouteBindRefusal(field, message, conflict));
}
