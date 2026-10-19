using System.Text.Json.Serialization;

namespace Matcad.Config;

/// <summary>
/// Configuration objects are persisted as JSON on the data volume (JSON is the
/// primary store for configs per project convention). Each carries a long Id so
/// routes can reference providers/authentications, plus lightweight audit info.
/// </summary>
public abstract class ConfigEntity
{
    public long Id { get; set; }
    public DateTime CreateDate { get; set; }
    public long? CreateUserId { get; set; }
    public DateTime? UpdateDate { get; set; }
    public long? UpdateUserId { get; set; }
    /// <summary>Which tool writes this object, e.g. "mattiverse" (set through the REST API).
    /// The lists show a "Managed by ..." badge for it; null for objects created by hand.
    /// Older config files do not have the field and load as null.</summary>
    public string? ManagedBy { get; set; }
}

/// <summary>A DNS provider (e.g. Netcup). Credentials are provider-specific key/value pairs.</summary>
public class ProviderConfig : ConfigEntity
{
    public string Name { get; set; } = "";
    /// <summary>Caddy DNS module id, e.g. "netcup".</summary>
    public string Type { get; set; } = "netcup";
    /// <summary>e.g. netcup: customer_number, api_key, api_password.</summary>
    public Dictionary<string, string> Credentials { get; set; } = new();
}

public enum AuthType
{
    BasicAuth = 0,
    /// <summary>Matcad forward-auth portal (login page + redirect back to endpoint).</summary>
    Matcad = 1
}

public class BasicAuthUser
{
    public string Username { get; set; } = "";
    /// <summary>bcrypt hash (Caddy accepts bcrypt for basic_auth).</summary>
    public string PasswordHash { get; set; } = "";
}

/// <summary>A reusable authentication that can be attached to any route.
/// For both Basic Auth and Matcad the login accounts live in <see cref="Users"/>;
/// the Brand* fields personalize the Matcad login portal per authentication.</summary>
public class AuthenticationConfig : ConfigEntity
{
    public string Name { get; set; } = "";
    public AuthType Type { get; set; } = AuthType.BasicAuth;
    public List<BasicAuthUser> Users { get; set; } = new();

    // --- Matcad login-portal branding (optional) ---
    public string BrandTitle { get; set; } = "";
    /// <summary>Logo as a URL or an embedded data: URI.</summary>
    public string BrandLogo { get; set; } = "";
    /// <summary>Logo placement relative to the title: "left" (beside) or "top" (above).</summary>
    public string BrandLogoLayout { get; set; } = "left";
    /// <summary>Accent colour, e.g. "#2f6feb".</summary>
    public string BrandColor { get; set; } = "";
    public string BrandText { get; set; } = "";
}

/// <summary>A header name and value: sent to the upstream, or demanded from the client. The value is a secret -
/// it is never logged, never put in an error message and never sent back through the REST API or the pages.</summary>
public class HeaderPair
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>A header of the answer to the visitor: set to a value, or removed (then the value is empty). Not a secret: it goes to every visitor.</summary>
public class ResponseHeader
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public bool Remove { get; set; }
}

/// <summary>
/// A route in the hierarchical domain tree. A route may be a wildcard host and
/// may have children (e.g. sub.example.com under *.example.com).
/// </summary>
public class RouteConfig : ConfigEntity
{
    private List<HeaderPair> _upstreamHeaders = new();
    private List<string> _allowedIps = new();
    private List<string> _blockedIps = new();
    private List<ResponseHeader> _responseHeaders = new();

    public string Name { get; set; } = "";
    /// <summary>Host matcher, e.g. app.example.com or *.example.com.</summary>
    public string Host { get; set; } = "";
    public bool Wildcard { get; set; }
    /// <summary>Reverse-proxy target, e.g. http://backend:8080.</summary>
    public string? Upstream { get; set; }
    /// <summary>For HTTPS upstreams: don't verify the backend's TLS certificate
    /// (accept self-signed / invalid certs). Ignored for plain-HTTP upstreams.</summary>
    public bool InsecureSkipVerify { get; set; }
    /// <summary>Redirect target. When set and no <see cref="Upstream"/> is given,
    /// the route responds with a redirect to this URL instead of proxying.</summary>
    public string? FallbackUrl { get; set; }
    /// <summary>Use a permanent (301) redirect for <see cref="FallbackUrl"/> instead
    /// of a temporary (302) one.</summary>
    public bool RedirectPermanent { get; set; }
    /// <summary>Append the path and query of the request to <see cref="FallbackUrl"/> (old.example.com/a?b to new.example.com/a?b).</summary>
    public bool RedirectKeepPath { get; set; }
    /// <summary>Answer 307/308 instead of 302/301: the browser repeats the method and the body (a POST stays a POST). Set on the route page.</summary>
    public bool RedirectKeepMethod { get; set; }
    /// <summary>The status code of the redirect: 301/302, or 308/307 when the method is kept.</summary>
    [JsonIgnore] public int RedirectStatus => RouteEditor.RedirectCode(RedirectPermanent, RedirectKeepMethod);
    public long? AuthenticationId { get; set; }
    /// <summary>Strip X-Frame-Options and CSP frame-ancestors from responses so the site can be
    /// embedded in an iframe (e.g. inside matOS). Off by default — keeps the app's clickjacking
    /// protection unless you opt in.</summary>
    public bool AllowEmbedding { get; set; }
    /// <summary>When set (&gt;0) this is a PORT-bound route: Caddy listens on this port and
    /// reverse-proxies to <see cref="Upstream"/> with no host matching (<see cref="Host"/> is
    /// optional/ignored for matching). Lets an app be reached DNS-free as http://&lt;host&gt;:&lt;port&gt;,
    /// which is how matOS's compatibility mode embeds apps when accessed via a bare hostname/IP where
    /// a wildcard domain like *.apps.localhost can't resolve.</summary>
    public int? ListenPort { get; set; }

    // --- Access and upstream (all optional, off by default; see RouteGuards for the rules). Older routes.json files and
    // --- backups do not have them and read as "off". The lists tolerate a JSON null, so a hand-edited file cannot break the generator.
    /// <summary>Host header sent to the upstream instead of the visitor's; for an https upstream also the TLS server name
    /// (SNI and certificate check). Only for reverse-proxy routes.</summary>
    public string? UpstreamHost { get; set; }
    /// <summary>Headers SET on the request to the upstream (they replace what the visitor sent). Values are secrets.</summary>
    public List<HeaderPair> UpstreamHeaders { get => _upstreamHeaders; set => _upstreamHeaders = value ?? new(); }
    /// <summary>Guard: a request without exactly this header and value is answered 403 before authentication, the
    /// framing strip and the proxy run. The value is a secret.</summary>
    public HeaderPair? RequireHeader { get; set; }
    /// <summary>Guard: a request whose direct peer (the TCP connection, not X-Forwarded-For) is not in this list of
    /// IP addresses or CIDR ranges is answered 403.</summary>
    public List<string> AllowedIps { get => _allowedIps; set => _allowedIps = value ?? new(); }
    /// <summary>Guard: a request whose direct peer is in this list of IP addresses or CIDR ranges is answered 403.</summary>
    public List<string> BlockedIps { get => _blockedIps; set => _blockedIps = value ?? new(); }
    /// <summary>True when a guard (required header, allowed or blocked IPs) is configured.</summary>
    [JsonIgnore] public bool HasGuards => RequireHeader != null || AllowedIps.Count > 0 || BlockedIps.Count > 0;

    // --- Options of the response (off by default; the page sets them, the REST API keeps what the route has).
    /// <summary>Requests that look like common attacks (SQL injection or script in the query, paths such as /.git or /.env) get 403.</summary>
    public bool BlockExploits { get; set; }
    /// <summary>Send Strict-Transport-Security, so browsers use HTTPS only for this host for a year.</summary>
    public bool Hsts { get; set; }
    /// <summary>Compress the responses of the upstream (zstd or gzip, whatever the browser takes).</summary>
    public bool Compress { get; set; }
    /// <summary>Headers set on or removed from every answer of the route (also those of the upstream, applied when the answer is written).</summary>
    public List<ResponseHeader> ResponseHeaders { get => _responseHeaders; set => _responseHeaders = value ?? new(); }

    /// <summary>DNS provider used for wildcard cert issuance (DNS-01).</summary>
    public long? ProviderId { get; set; }
    /// <summary>Optional per-domain ACME contact email for this route's certificate.
    /// Overrides the global <see cref="MatcadSettings.AcmeEmail"/> when set.</summary>
    public string? AcmeEmail { get; set; }
    public bool Enabled { get; set; } = true;

    // --- Derived routes (e.g. from Docker); not persisted to routes.json. ---
    /// <summary>null = manually managed; "docker" = derived from a container.</summary>
    [JsonIgnore] public string? Source { get; set; }
    /// <summary>Origin detail, e.g. the container name.</summary>
    [JsonIgnore] public string? SourceDetail { get; set; }
    [JsonIgnore] public bool IsDerived => Source != null;
}

public class MatcadSettings
{
    /// <summary>Parent domain shared by protected routes, e.g. "example.com".
    /// The Matcad forward-auth cookie is scoped to ".{BaseDomain}" so a single
    /// login works across all subdomains.</summary>
    public string BaseDomain { get; set; } = "";
    /// <summary>How forward-auth presents the login to unauthenticated users:
    /// "inline"  - login served on the protected host itself (hides the Matcad host),
    /// "redirect" - redirect to <see cref="AuthPortalUrl"/> / Matcad host,
    /// "unauthorized" - return a plain 401 (no login form).</summary>
    public string PortalMode { get; set; } = "inline";
    /// <summary>Absolute URL of the Matcad login portal (redirect mode only).
    /// If empty, a relative /auth/portal is used.</summary>
    public string AuthPortalUrl { get; set; } = "";
    /// <summary>Hostname under which Matcad exposes itself through Caddy, e.g.
    /// "matcad.example.com". When set (and <see cref="SystemRouteEnabled"/>),
    /// a read-only "system" route to matcad:4433 is generated, the UI becomes
    /// reachable via this domain, and the login portal is served from it.</summary>
    public string MatcadHost { get; set; } = "";
    /// <summary>Whether the self-exposing system route is generated.</summary>
    public bool SystemRouteEnabled { get; set; } = true;
    /// <summary>False on a fresh install -> the first-run setup wizard is shown.
    /// Set true once the wizard is finished (or for upgraded installs that already
    /// have configuration).</summary>
    public bool SetupCompleted { get; set; }
    public int LogRetentionDays { get; set; } = 30;
    /// <summary>Hard upper bound on stored request-log rows (0 = unlimited).
    /// Bounds growth under high traffic even within the retention window.</summary>
    public long LogRetentionMaxRows { get; set; } = 1_000_000;
    public string CaddyAdminUrl { get; set; } = "http://caddy:2019";
    /// <summary>Email used for ACME/Let's Encrypt registration.</summary>
    public string AcmeEmail { get; set; } = "";
    /// <summary>Seconds to wait after writing the DNS-01 TXT record before asking the
    /// CA to validate (gives slow DNS providers like netcup time to propagate).
    /// 0 = Caddy default.</summary>
    public int AcmePropagationDelaySeconds { get; set; }
    /// <summary>Max seconds Caddy waits for the DNS-01 record to appear on the
    /// authoritative nameservers. 0 = Caddy default; -1 = skip the propagation
    /// check entirely (useful when a provider is slow but the CA can still see it).</summary>
    public int AcmePropagationTimeoutSeconds { get; set; }
    /// <summary>Extra Caddy JSON deep-merged into the generated config (objects
    /// merge, arrays concatenate). Escape hatch for anything Matcad doesn't model
    /// directly; also where the Caddyfile importer stores unmappable parts.</summary>
    public string RawCaddyJson { get; set; } = "";
    /// <summary>What Caddy answers for a host that no route has (see <see cref="UnknownHostModes"/>): "" = Caddy's own behaviour (plain HTTP
    /// is sent to HTTPS, where the handshake fails without a certificate), "notfound" = a small 404 page, "redirect" = to
    /// <see cref="UnknownHostRedirect"/>, "drop" = close the connection without an answer.</summary>
    public string UnknownHost { get; set; } = "";
    /// <summary>The target of the "redirect" mode of <see cref="UnknownHost"/>: an http(s) address.</summary>
    public string UnknownHostRedirect { get; set; } = "";
    public DockerSettings Docker { get; set; } = new();

    /// <summary>Effective login-portal base URL: explicit AuthPortalUrl, else
    /// derived from the system route host, else empty (relative /auth/portal).</summary>
    /// <summary>The modes of <see cref="UnknownHost"/>.</summary>
    public static readonly string[] UnknownHostModes = ["", "notfound", "redirect", "drop"];

    public string EffectivePortalUrl()
    {
        if (!string.IsNullOrWhiteSpace(AuthPortalUrl)) return AuthPortalUrl.TrimEnd('/');
        if (SystemRouteEnabled && !string.IsNullOrWhiteSpace(MatcadHost)) return $"https://{MatcadHost.Trim()}";
        return "";
    }
}

/// <summary>
/// Docker discovery mode: read labels/names from a Docker host and derive routes.
/// </summary>
public class DockerSettings
{
    public bool Enabled { get; set; }
    /// <summary>Docker Engine endpoint. Local socket by default.</summary>
    public string Endpoint { get; set; } = "unix:///var/run/docker.sock";
    /// <summary>Base domain for auto-naming (containername.&lt;BaseDomain&gt;).
    /// Falls back to the global BaseDomain when empty.</summary>
    public string BaseDomain { get; set; } = "";
    /// <summary>Only bind containers that carry matcad.enable=true (opt-in).</summary>
    public bool RequireEnableLabel { get; set; } = true;
}

/// <summary>Container label keys understood by the Docker discovery.</summary>
public static class DockerLabels
{
    public const string Enable = "matcad.enable";
    public const string Host = "matcad.host";
    public const string Port = "matcad.port";
    public const string Auth = "matcad.auth";
}
