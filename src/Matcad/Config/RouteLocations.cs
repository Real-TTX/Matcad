using System.Text.RegularExpressions;

namespace Matcad.Config;

/// <summary>
/// The rules for the custom locations of a route (a path that goes to another upstream, or redirects). Shared by the route page
/// (through <see cref="RouteEditor.Apply"/>) and the Caddy config generator, which refuses to serve a route whose stored locations break them.
/// </summary>
public static partial class RouteLocations
{
    private const int MaxLocations = 20;
    private const int MaxPathLength = 200;

    /// <summary>
    /// Normalises the path of a location: starts with <c>/</c>, no trailing slash. Only plain segments: letters, digits and
    /// <c>- . _ ~ ! $ &amp; ' ( ) + , ; = : @</c>. Refused are <c>*</c> (Caddy's path matcher reads it as a wildcard), <c>%</c> (it has
    /// its own escape rules there), <c>{ }</c> (placeholders), <c>? #</c>, spaces, empty, <c>.</c> and <c>..</c> segments, and <c>/auth</c>
    /// (the login pages of the Matcad portal live there).
    /// </summary>
    public static (string Path, string? Error) NormalizePath(string? value)
    {
        var path = (value ?? "").Trim();
        if (path.Length > 1) path = path.TrimEnd('/');
        if (path.Length == 0) return ("", "Location: the path is empty.");
        if (path == "/") return (path, "Location '/': that is the route itself; use a path like /api.");
        if (path.Length > MaxPathLength || !PathPattern().IsMatch(path))
            return (path, $"Location '{Short(path)}': the path must look like /api or /static/img (letters, digits and - . _ ~ ! $ & ' ( ) + , ; = : @; no *, %, {{ }}, ? or spaces).");
        if (path.Split('/').Any(s => s is "." or ".."))
            return (path, $"Location '{Short(path)}': the path must not contain . or .. segments.");
        if (path.Equals("/auth", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/auth/", StringComparison.OrdinalIgnoreCase))
            return (path, $"Location '{Short(path)}': /auth belongs to the Matcad login pages.");
        return (path, null);
    }

    /// <summary>Checks the locations (as stored, or as the editor is about to store them): paths, no path twice, and exactly one target each.</summary>
    public static string? Check(IReadOnlyList<RouteLocation>? locations)
    {
        if (locations == null || locations.Count == 0) return null;
        if (locations.Count > MaxLocations) return $"Locations: at most {MaxLocations}.";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // Caddy's path matcher ignores case
        foreach (var l in locations)
        {
            var (path, error) = NormalizePath(l.Path);
            if (error != null) return error;
            if (path != l.Path) return $"Location '{Short(l.Path)}': the path is not in its normal form.";
            if (!seen.Add(path)) return $"Locations: '{Short(path)}' is listed twice.";
            var hasUpstream = !string.IsNullOrEmpty(l.Upstream);
            var hasRedirect = !string.IsNullOrEmpty(l.RedirectUrl);
            if (hasUpstream == hasRedirect) return $"Location '{Short(path)}': give either an upstream or a redirect target.";
            if (hasUpstream && !RouteEditor.IsUpstream(l.Upstream!))
                return $"Location '{Short(path)}': the upstream must look like backend:8080 or http(s)://backend:8080.";
            if (hasRedirect)
            {
                if (RouteEditor.CheckRedirectTarget(l.RedirectUrl!) is { } redirectError) return $"Location '{Short(path)}': {redirectError}";
                if (!RouteEditor.RedirectCodes.Contains(l.RedirectCode)) return $"Location '{Short(path)}': the redirect code must be 301, 302, 307 or 308.";
                if (l.StripPath) return $"Location '{Short(path)}': only a location with an upstream can strip its path.";
            }
        }
        return null;
    }

    /// <summary>The order Caddy tries them in: the most specific path first (more segments, then the longer one), so /api/v2 wins over /api.</summary>
    public static IEnumerable<RouteLocation> MostSpecificFirst(IEnumerable<RouteLocation> locations) =>
        locations.OrderByDescending(l => l.Path.Count(c => c == '/')).ThenByDescending(l => l.Path.Length).ThenBy(l => l.Path, StringComparer.Ordinal);

    private static string Short(string? text) => text is { Length: > 40 } ? text[..40] + "..." : text ?? "";

    [GeneratedRegex(@"^(/[A-Za-z0-9._~!$&'()+,;=:@-]+)+\z")]
    private static partial Regex PathPattern();
}
