namespace Matcad.Config;

/// <summary>
/// Seeds illustrative example data on first start (when no providers, routes or
/// authentications exist yet). Example routes are created <b>disabled</b>, so
/// the UI is fully populated to explore while nothing is pushed to Caddy.
/// </summary>
public static class ExampleData
{
    public static bool IsEmpty(ConfigStore store) =>
        store.Providers.Count == 0 && store.Routes.Count == 0 && store.Authentications.Count == 0;

    public static void Seed(ConfigStore store, long? actorId)
    {
        // --- Settings ---
        store.SaveSettings(new MatcadSettings
        {
            BaseDomain = "example.com",
            AuthPortalUrl = "https://auth.example.com",
            // Intentionally empty: Let's Encrypt rejects contact emails on the
            // example.com domain, which would break ALL certificate issuance.
            // Caddy registers the ACME account fine without a contact email.
            AcmeEmail = "",
            LogRetentionDays = 30,
            CaddyAdminUrl = "http://caddy:2019",
            // Loading demo data implies setup is done; don't drop back into the wizard.
            SetupCompleted = true
        });

        // --- Provider (DNS) ---
        var netcup = new ProviderConfig
        {
            Name = "Netcup (Beispiel)",
            Type = "netcup",
            Credentials = new()
            {
                ["customer_number"] = "12345",
                ["api_key"] = "DEMO-API-KEY",
                ["api_password"] = "DEMO-API-PASSWORD"
            }
        };
        store.UpsertProvider(netcup, actorId);

        // --- Authentications ---
        var intern = new AuthenticationConfig
        {
            Name = "Intern",
            Type = AuthType.BasicAuth,
            Users = new()
            {
                new BasicAuthUser { Username = "gast", PasswordHash = BCrypt.Net.BCrypt.HashPassword("geheim") }
            }
        };
        store.UpsertAuthentication(intern, actorId);

        var portal = new AuthenticationConfig
        {
            Name = "Portal",
            Type = AuthType.Matcad,
            BrandTitle = "Example Portal",
            BrandText = "Please sign in to continue",
            Users = new()
            {
                new BasicAuthUser { Username = "kunde", PasswordHash = BCrypt.Net.BCrypt.HashPassword("kunde") }
            }
        };
        store.UpsertAuthentication(portal, actorId);

        // --- Routes (disabled examples showing the domain hierarchy) ---
        store.UpsertRoute(new RouteConfig
        {
            Name = "Startseite", Host = "example.com",
            FallbackUrl = "https://www.example.com", Enabled = false
        }, actorId);

        store.UpsertRoute(new RouteConfig
        {
            Name = "App", Host = "app.example.com",
            Upstream = "http://app:8080", AuthenticationId = portal.Id, Enabled = false
        }, actorId);

        store.UpsertRoute(new RouteConfig
        {
            Name = "API", Host = "api.example.com",
            Upstream = "http://api:8080", AuthenticationId = intern.Id, Enabled = false
        }, actorId);

        store.UpsertRoute(new RouteConfig
        {
            Name = "Wildcard", Host = "*.example.com", Wildcard = true,
            FallbackUrl = "https://www.example.com", ProviderId = netcup.Id, Enabled = false
        }, actorId);
    }
}
