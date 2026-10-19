using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Matcad.Config;

/// <summary>
/// The rules for the access properties of a route, shared by the edit page, the REST API and the Caddy config generator:
/// the Host header to the upstream, headers set on the upstream request, the required header and the allowed IP addresses.
///
/// Two traps shape most of them. Caddy's header matcher reads <c>*</c> as a wildcard and replaces <c>{...}</c> placeholders in
/// the value it compares with, so a secret containing either would quietly match more than it says (or something else
/// entirely): such values are refused. And a secret that is too short is worth nothing, so a required header needs at least
/// <see cref="MinSecretLength"/> characters. Error messages name the field and the reason and never repeat a value.
/// </summary>
public static partial class RouteGuards
{
    /// <summary>The shortest value a required header may have.</summary>
    public const int MinSecretLength = 16;

    private const int MaxHeaders = 20;
    private const int MaxIps = 200;
    private const int MaxNameLength = 100;
    private const int MaxValueLength = 1024;

    // Host has its own field, the other three are set by the HTTP stack itself and would corrupt the message.
    private static readonly string[] ForbiddenNames = ["Host", "Content-Length", "Transfer-Encoding", "Connection"];

    /// <summary>Checks a header name: an HTTP token that starts with a letter or digit (Caddy gives a leading
    /// <c>-</c> or <c>+</c> a meaning of its own), and not one of the names the HTTP stack owns.</summary>
    public static string? CheckName(string? name, string label)
    {
        if (string.IsNullOrEmpty(name)) return $"{label}: the header name is empty.";
        if (name.Length > MaxNameLength || !HeaderName().IsMatch(name))
            return $"{label}: '{Short(name)}' is not a usable header name (letters, digits and - _ . ! # $ % & ' * + ^ ` | ~, starting with a letter or digit).";
        if (ForbiddenNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            return name.Equals("Host", StringComparison.OrdinalIgnoreCase)
                ? $"{label}: the header 'Host' cannot be used here; use the Host header field of the route."
                : $"{label}: the header '{name}' cannot be used, the HTTP stack sets it itself.";
        return null;
    }

    /// <summary>Checks a header value. <paramref name="asMatch"/> is for a value the proxy compares requests against.</summary>
    private static string? CheckValue(string? value, string label, bool asMatch)
    {
        if (string.IsNullOrEmpty(value)) return $"{label}: the value is empty.";
        if (value.Length > MaxValueLength) return $"{label}: the value is longer than {MaxValueLength} characters.";
        if (asMatch)
        {
            // Visible ASCII only (0x21-0x7E). HTTP trims the spaces around a value it receives, so a secret with a space at its edge could
            // never match and would lock every caller out; anything else in a secret is a copy-and-paste accident.
            if (value.Any(c => c is < '!' or > '~')) return $"{label}: the value may only contain visible ASCII characters (0x21-0x7E, no spaces).";
        }
        else
        {
            if (value.Any(char.IsControl)) return $"{label}: the value must not contain control characters.";
            if (value[0] == ' ' || value[^1] == ' ') return $"{label}: the value must not start or end with a space.";
        }
        if (value.AsSpan().IndexOfAny('{', '}') >= 0)
            return $"{label}: the value must not contain {{ or }} (Caddy would read it as a placeholder).";
        if (!asMatch) return null;
        if (value.Contains('*')) return $"{label}: the value must not contain * (Caddy's header match treats it as a wildcard).";
        if (value.Length < MinSecretLength) return $"{label}: the value must be at least {MinSecretLength} characters, a shorter secret is not worth checking.";
        return null;
    }

    /// <summary>Checks the required header. Null means there is none.</summary>
    public static string? CheckRequireHeader(HeaderPair? header)
    {
        const string label = "Required header";
        if (header == null) return null;
        return CheckName(header.Name, label) ?? CheckValue(header.Value, label, asMatch: true);
    }

    /// <summary>Checks the headers set on the upstream request: names, no duplicates, values.</summary>
    public static string? CheckUpstreamHeaders(IReadOnlyList<HeaderPair>? headers)
    {
        if (headers == null || headers.Count == 0) return null;
        if (headers.Count > MaxHeaders) return $"Upstream headers: at most {MaxHeaders} headers.";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in headers)
        {
            var label = $"Upstream header '{Short(h.Name)}'";
            var error = CheckName(h.Name, "Upstream header") ?? CheckValue(h.Value, label, asMatch: false);
            if (error != null) return error;
            if (!seen.Add(h.Name)) return $"Upstream headers: '{h.Name}' is listed twice.";
        }
        return null;
    }

    // Headers of the answer that describe the message itself: set or removed by hand they would break it.
    private static readonly string[] ForbiddenResponseNames =
        ["Content-Length", "Transfer-Encoding", "Connection", "Keep-Alive", "Upgrade", "Trailer", "Content-Encoding"];

    /// <summary>
    /// Checks the headers of the answer (set or removed). The value goes into the HTTP answer as it is, so it may only hold visible ASCII and
    /// spaces: no CR/LF (header injection), no other control character, nothing Caddy would read as a placeholder (<c>{</c>, <c>}</c>; a
    /// <c>{env.*}</c> would hand out Caddy's environment to every visitor). A name is a token without <c>*</c> (Caddy's delete reads it as a wildcard).
    /// </summary>
    public static string? CheckResponseHeaders(IReadOnlyList<ResponseHeader>? headers)
    {
        const string label = "Response header";
        if (headers == null || headers.Count == 0) return null;
        if (headers.Count > MaxHeaders) return $"Response headers: at most {MaxHeaders} headers.";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in headers)
        {
            var name = h.Name ?? "";
            if (name.Length == 0) return $"{label}: the header name is empty.";
            if (name.Length > MaxNameLength || !HeaderName().IsMatch(name) || name.Contains('*'))
                return $"{label}: '{Short(name)}' is not a usable header name (letters, digits and - _ . ! # $ % & ' + ^ ` | ~, starting with a letter or digit).";
            if (ForbiddenResponseNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                return $"{label}: '{name}' cannot be set or removed, it describes the HTTP message itself.";
            if (!seen.Add(name)) return $"Response headers: '{Short(name)}' is listed twice.";
            var value = h.Value ?? "";
            if (h.Remove)
            {
                if (value.Length > 0) return $"{label} '{Short(name)}': a header that is removed has no value.";
                continue;
            }
            if (value.Length == 0) return $"{label} '{Short(name)}': enter a value (or remove the header).";
            if (value.Length > MaxValueLength) return $"{label} '{Short(name)}': the value is longer than {MaxValueLength} characters.";
            if (value.Any(c => c is < ' ' or > '~')) return $"{label} '{Short(name)}': the value may only contain visible ASCII characters and spaces (no line breaks).";
            if (value[0] == ' ' || value[^1] == ' ') return $"{label} '{Short(name)}': the value must not start or end with a space.";
            if (value.AsSpan().IndexOfAny('{', '}') >= 0)
                return $"{label} '{Short(name)}': the value must not contain {{ or }} (Caddy would read it as a placeholder).";
        }
        return null;
    }

    /// <summary>A response header must not fight a switch of the route that writes the same header.</summary>
    public static string? CheckResponseHeaderClash(IReadOnlyList<ResponseHeader>? headers, bool hsts, bool allowEmbedding)
    {
        foreach (var h in headers ?? [])
        {
            if (hsts && h.Name.Equals("Strict-Transport-Security", StringComparison.OrdinalIgnoreCase))
                return "Response header 'Strict-Transport-Security': the HSTS switch of the route writes it. Turn HSTS off to set your own.";
            if (allowEmbedding && !h.Remove && h.Name.Equals("X-Frame-Options", StringComparison.OrdinalIgnoreCase))
                return "Response header 'X-Frame-Options': the route allows embedding, which removes it. Turn that off to set it.";
        }
        return null;
    }

    /// <summary>The required header is removed from the request before it goes to the upstream (the app behind has no use for the secret,
    /// and neither has a plugin or a log there). Caddy deletes after it sets, so an upstream header of the same name would be deleted
    /// again: the two cannot share a name.</summary>
    public static string? CheckNameClash(HeaderPair? required, IReadOnlyList<HeaderPair>? upstream) =>
        required != null && upstream?.Any(h => h.Name.Equals(required.Name, StringComparison.OrdinalIgnoreCase)) == true
            ? $"Required header '{Short(required.Name)}' is also listed as an upstream header. The required header is removed from the request before it goes to the upstream, so the two cannot share a name."
            : null;

    /// <summary>Normalises the Host header for the upstream (lower case, host or host:port) or says what is wrong with it.
    /// Empty means "none".</summary>
    public static (string? Host, string? Error) NormalizeUpstreamHost(string? value)
    {
        const string label = "Upstream host";
        var text = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(text)) return (null, null);
        if (text.Length > 262) return (null, $"{label}: too long.");
        var (name, port, ok) = SplitHost(text);
        if (!ok || !IsValidHostPart(name) || (port != null && !(int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is >= 1 and <= 65535)))
            return (null, $"{label}: '{Short(text)}' must be a plain host name like app.example.com, optionally with :port (no http://, path or spaces).");
        return (text, null);
    }

    /// <summary>The name for the TLS handshake with an https upstream: the Host header without a port or brackets.</summary>
    public static string TlsServerName(string upstreamHost) => SplitHost(upstreamHost.Trim().ToLowerInvariant()).Name;

    private static (string Name, string? Port, bool Ok) SplitHost(string text)
    {
        var bracket = Bracketed().Match(text);
        if (bracket.Success) return (bracket.Groups[1].Value, bracket.Groups[2].Success ? bracket.Groups[2].Value : null, true);
        var colon = text.IndexOf(':');
        if (colon < 0) return (text, null, true);
        // Anything with a second colon (a bare IPv6 address) is ambiguous: it has to be written in brackets.
        return text.IndexOf(':', colon + 1) >= 0 ? (text, null, false) : (text[..colon], text[(colon + 1)..], true);
    }

    private static bool IsValidHostPart(string name) =>
        name.Contains(':') ? IPAddress.TryParse(name, out _) : RouteEditor.IsHostName(name);

    /// <summary>Normalises the list of allowed addresses (single IPs and CIDR ranges, IPv4 and IPv6) or says which entry is not one.
    /// Blank entries are skipped, duplicates collapse, a range is written with its network address.</summary>
    public static (List<string> Ips, string? Error) NormalizeIps(IEnumerable<string>? entries, string label = "Allowed IPs")
    {
        var result = new List<string>();
        foreach (var entry in entries ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            if (!TryNormalizeIp(entry.Trim(), out var normalized))
                return (result, $"{label}: '{Short(entry.Trim())}' is not an IP address or CIDR range (like 203.0.113.7, 10.0.0.0/8 or fd00::/8).");
            if (!result.Contains(normalized)) result.Add(normalized);
            if (result.Count > MaxIps) return (result, $"{label}: at most {MaxIps} entries.");
        }
        return (result, null);
    }

    private static bool TryNormalizeIp(string text, out string normalized)
    {
        normalized = "";
        if (text.Length > 64 || !IpText().IsMatch(text)) return false;
        var slash = text.IndexOf('/');
        var addressText = slash < 0 ? text : text[..slash];
        if (!IPAddress.TryParse(addressText, out var address)) return false;
        // TryParse is lenient ("10" is 0.0.0.10, "1.2.3" is 1.2.0.3): only the full notation counts.
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && addressText.Count(c => c == '.') != 3) return false;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && !addressText.Contains(':')) return false;
        // The peer address Caddy compares is always the IPv4 form for an IPv4 client, so a mapped address would never match.
        if (address.IsIPv4MappedToIPv6)
        {
            if (slash >= 0) return false;
            address = address.MapToIPv4();
        }
        if (slash < 0) { normalized = address.ToString(); return true; }

        var maxPrefix = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        if (!int.TryParse(text.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) || prefix > maxPrefix) return false;
        var bytes = address.GetAddressBytes();
        for (var bit = prefix; bit < bytes.Length * 8; bit++) bytes[bit / 8] &= (byte)~(0x80 >> (bit % 8));
        normalized = new IPNetwork(new IPAddress(bytes), prefix).ToString();
        return true;
    }

    /// <summary>
    /// Checks every access property of a stored route. The generator calls this so a route that carries invalid values
    /// (a hand-edited file, a backup from somewhere else) is not served at all instead of being served without its guard.
    /// </summary>
    public static string? Check(RouteConfig route) =>
        NormalizeUpstreamHost(route.UpstreamHost).Error
        ?? CheckUpstreamHeaders(route.UpstreamHeaders)
        ?? CheckRequireHeader(route.RequireHeader)
        ?? CheckNameClash(route.RequireHeader, route.UpstreamHeaders)
        ?? NormalizeIps(route.AllowedIps).Error
        ?? NormalizeIps(route.BlockedIps, BlockedLabel).Error
        ?? CheckResponseHeaders(route.ResponseHeaders)
        ?? RouteLocations.Check(route.Locations);

    /// <summary>The label of the blocked addresses in messages.</summary>
    public const string BlockedLabel = "Blocked IPs";

    /// <summary>
    /// The value of a header left empty on a form (or missing in an API call) means "keep the saved one": fills it from
    /// <paramref name="stored"/> by name. A header that has no saved value to keep is an error.
    /// </summary>
    public static string? KeepStoredValues(IEnumerable<HeaderPair> posted, IEnumerable<HeaderPair>? stored, string label)
    {
        foreach (var h in posted)
        {
            if (!string.IsNullOrEmpty(h.Value)) continue;
            var old = stored?.FirstOrDefault(s => s.Name.Equals(h.Name, StringComparison.OrdinalIgnoreCase));
            if (old is null || string.IsNullOrEmpty(old.Value))
                return $"{label} '{Short(h.Name)}': enter a value.";
            h.Value = old.Value;
        }
        return null;
    }

    /// <summary>Splits the text of the allowed-IPs box into entries: one per line, commas and semicolons work as well.</summary>
    public static List<string> SplitIps(string? text) =>
        (text ?? "").Split(['\n', '\r', ',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>The name as Go's net/http writes it (first letter and every letter after a hyphen upper case, the rest lower case).
    /// Caddy sets a header under the key it is given, so a name that is not canonical would sit next to the header
    /// the visitor sent instead of replacing it.</summary>
    public static string CanonicalName(string name)
    {
        var chars = name.ToCharArray();
        var upper = true;
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (upper && c is >= 'a' and <= 'z') chars[i] = (char)(c - 32);
            else if (!upper && c is >= 'A' and <= 'Z') chars[i] = (char)(c + 32);
            upper = c == '-';
        }
        return new string(chars);
    }

    // A message shows a name or an address, never a value; a very long one is cut.
    private static string Short(string? text) => text is { Length: > 40 } ? text[..40] + "..." : text ?? "";

    // \z, not $: $ also matches before a final line break, and a stored name is not trimmed.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9!#$%&'*+.^_`|~-]*\z")]
    private static partial Regex HeaderName();

    [GeneratedRegex(@"^\[([0-9a-f:.]+)\](?::(\d{1,5}))?$")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"^[0-9a-fA-F:.]+(/\d{1,3})?$")]
    private static partial Regex IpText();
}
