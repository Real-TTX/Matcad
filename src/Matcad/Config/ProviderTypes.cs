namespace Matcad.Config;

/// <summary>
/// Registry of supported DNS provider types. Each type maps to a Caddy DNS
/// module (built into the caddy image via xcaddy) and declares the credential
/// fields it needs. Add a new provider by adding an entry here and the matching
/// --with module in caddy/Dockerfile.
/// </summary>
public static class ProviderTypes
{
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
    };

    public static ProviderType? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    public static string DisplayName(string id) => Find(id)?.DisplayName ?? id;
}
