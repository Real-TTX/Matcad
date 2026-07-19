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

/// <summary>
/// A route in the hierarchical domain tree. A route may be a wildcard host and
/// may have children (e.g. sub.example.com under *.example.com).
/// </summary>
public class RouteConfig : ConfigEntity
{
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
    public long? AuthenticationId { get; set; }
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
    public DockerSettings Docker { get; set; } = new();

    /// <summary>Effective login-portal base URL: explicit AuthPortalUrl, else
    /// derived from the system route host, else empty (relative /auth/portal).</summary>
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
