namespace Matcad.Config;

/// <summary>
/// Registry of known DNS provider types for the UI. Each Id equals the Caddy DNS
/// module name (the module must be compiled into the caddy binary - see
/// CADDY_DNS_MODULES in .env). The generator uses the provider's Type directly as
/// the module name, so the "Custom" type below allows ANY compiled-in module even
/// if it isn't listed here.
/// </summary>
public static class ProviderTypes
{
    public const string CustomId = "custom";

    public record CredentialField(string Key, string Label, bool Secret = true);

    public record ProviderType(string Id, string DisplayName, string CaddyModule, List<CredentialField> Fields);

    public static readonly List<ProviderType> All = new()
    {
        new ProviderType("netcup", "Netcup", "netcup", new()
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
}
