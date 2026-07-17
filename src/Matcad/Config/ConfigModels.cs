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

/// <summary>A reusable authentication that can be attached to any route.</summary>
public class AuthenticationConfig : ConfigEntity
{
    public string Name { get; set; } = "";
    public AuthType Type { get; set; } = AuthType.BasicAuth;
    public List<BasicAuthUser> Users { get; set; } = new();
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
    /// <summary>Used when no upstream matches / as a catch-all redirect.</summary>
    public string? FallbackUrl { get; set; }
    public long? AuthenticationId { get; set; }
    /// <summary>DNS provider used for wildcard cert issuance (DNS-01).</summary>
    public long? ProviderId { get; set; }
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
    /// <summary>Absolute URL of the Matcad login portal, e.g.
    /// "https://auth.example.com". Used by forward-auth to redirect
    /// unauthenticated users. If empty, a relative /auth/portal is used.</summary>
    public string AuthPortalUrl { get; set; } = "";
    public int LogRetentionDays { get; set; } = 30;
    public string CaddyAdminUrl { get; set; } = "http://caddy:2019";
    /// <summary>Email used for ACME/Let's Encrypt registration.</summary>
    public string AcmeEmail { get; set; } = "";
    public DockerSettings Docker { get; set; } = new();
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
