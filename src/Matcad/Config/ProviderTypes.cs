namespace Matcad.Config;

/// <summary>
/// Registry of known DNS provider types for the UI. A type's Id is the value the UI
/// stores, and <see cref="Module"/> maps it to the Caddy DNS module name (the module
/// must be compiled into the caddy binary - see CADDY_DNS_MODULES in .env). Id and
/// CaddyModule are the same for almost every type; they differ for the two netcup
/// entries, which are two UI faces of the one "netcup" module (the new CloudDNS REST
/// API and the legacy CCP API, told apart by whether the API password is set). The
/// generator uses <see cref="Module"/>, so the "Custom" type below (an unknown Id)
/// still maps straight to any compiled-in module.
/// </summary>
public static class ProviderTypes
{
    public const string CustomId = "custom";

    public record CredentialField(string Key, string Label, bool Secret = true);

    public record ProviderType(string Id, string DisplayName, string CaddyModule, List<CredentialField> Fields);

    public static readonly List<ProviderType> All = new()
    {
        // netcup's new CloudDNS REST API: one long-lived Bearer key (CCP -> Master Data
        // -> API -> "API Keys"), no customer number or password. Same Caddy module as the
        // legacy type below; the module uses REST because the API password is left empty.
        new ProviderType("netcup_cloud", "Netcup", "netcup", new()
        {
            new CredentialField("api_key", "API key"),
        }),
        new ProviderType("netcup", "Netcup legacy", "netcup", new()
        {
            new CredentialField("customer_number", "Customer number", Secret: false),
            new CredentialField("api_key", "API key"),
            new CredentialField("api_password", "API password"),
        }),
        new ProviderType("cloudflare", "Cloudflare", "cloudflare", new()
        {
            new CredentialField("api_token", "API token"),
        }),
        new ProviderType("digitalocean", "DigitalOcean", "digitalocean", new()
        {
            new CredentialField("auth_token", "API token"),
        }),
        new ProviderType("hetzner", "Hetzner", "hetzner", new()
        {
            new CredentialField("api_token", "API token"),
        }),
        new ProviderType("route53", "AWS Route 53", "route53", new()
        {
            new CredentialField("access_key_id", "Access key ID", Secret: false),
            new CredentialField("secret_access_key", "Secret access key"),
            new CredentialField("region", "Region", Secret: false),
        }),
        new ProviderType("gandi", "Gandi", "gandi", new()
        {
            new CredentialField("bearer_token", "API / PAT token"),
        }),
        new ProviderType("desec", "deSEC", "desec", new()
        {
            new CredentialField("token", "API token"),
        }),
        new ProviderType("ovh", "OVH", "ovh", new()
        {
            new CredentialField("endpoint", "Endpoint (e.g. ovh-eu)", Secret: false),
            new CredentialField("application_key", "Application key", Secret: false),
            new CredentialField("application_secret", "Application secret"),
            new CredentialField("consumer_key", "Consumer key"),
        }),
    };

    public static ProviderType? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    public static string DisplayName(string id) => Find(id)?.DisplayName ?? id;

    /// <summary>The Caddy DNS module a stored type maps to. A known type uses its CaddyModule
    /// (so the two netcup Ids both resolve to "netcup"); an unknown Id (a custom module) is the
    /// module itself.</summary>
    public static string Module(string id) => Find(id)?.CaddyModule ?? id;
}
