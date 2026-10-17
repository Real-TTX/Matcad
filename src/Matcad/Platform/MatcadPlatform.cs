using System.Reflection;
using System.Text.Json;
using Matcad.Config;
using Matcad.Services;
using MatPlatform;

namespace Matcad.Platform;

/// <summary>
/// Matcad as an app of the platform contract (MatDesign docs/PLATFORM.md), on the shared package MatPlatform (the folder <c>MatPlatform/</c>, a copy):
/// how Mattiverse shows it (<see cref="Describe"/>: tiles, settings, buttons), what its values are and what the buttons do. Everything comes from the
/// services (<see cref="ProxyStatusService"/>, <see cref="ConfigStore"/>, <see cref="CaddyService"/>, <see cref="ProxyMonitor"/>, <see cref="AuthService"/>).
/// The engine API of Mattiverse (<c>/api/v1</c>) is separate and unchanged. Texts are English, or German when the caller asks for it.
/// </summary>
public sealed class MatcadPlatform(
    ProxyStatusService status,
    ConfigStore store,
    CaddyService caddy,
    ProxyMonitor monitor,
    AuthService auth,
    ILogger<MatcadPlatform> log) : IMatPlatformState, IMatPlatformConfiguration, IMatPlatformActions, IMatPlatformHealth
{
    /// <summary>The id of Matcad in the platform (MatDesign AppRegistry) and the default client id in MatIdentity.</summary>
    public const string AppId = "matcad";

    public const string Accent = "#2f6feb";

    /// <summary>A certificate that expires sooner than this is a warning, sooner than <see cref="ExpiryError"/> an error (Caddy renews 30 days ahead).</summary>
    public static readonly TimeSpan ExpiryWarning = TimeSpan.FromDays(14);

    public static readonly TimeSpan ExpiryError = TimeSpan.FromDays(3);

    public static readonly IReadOnlyList<string> PortalModes = ["inline", "redirect", "unauthorized"];

    /// <summary>A host name, optionally a wildcard-free domain; empty is allowed (not set).</summary>
    public const string HostPattern = @"^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)*$";

    public static string Version { get; } = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "local";

    /// <summary>The MatApiDefinition of Matcad (<c>GET /api</c>); <c>auth</c> and <c>endpoints</c> are filled in by MatPlatform.</summary>
    public static void Describe(MatPlatformBuilder o)
    {
        var T = o.T;

        o.App(AppId, "Matcad", T("Reverse-Proxy mit automatischem HTTPS", "Reverse proxy with automatic HTTPS"), Version, null, Accent);

        o.Page("overview", T("Übersicht", "Overview"), "dashboard",
            MatSection.Tiles("routes", "upstreamErrors", "certificates", "certificateExpiry", "certificateErrors", "providers", "caddy"),
            MatSection.Actions("open", "openRoutes", "openCertificates", "reloadCaddy", "checkCertificates"));
        o.Page("settings", T("Einstellungen", "Settings"), "settings",
            MatSection.Form(T("Domains", "Domains"), "baseDomain", "matcadHost"),
            MatSection.Form(T("Zertifikate und Anmeldung", "Certificates and sign-in"), "acmeEmail", "portalMode"));

        o.Tile("routes", T("Aktive Routen", "Active routes"), icon: "route");
        o.Tile(new MatStateItem("upstreamErrors", T("Hosts mit unerreichbarem Ziel (1 h)", "Hosts with an unreachable upstream (1 h)"))
        {
            Icon = "alert",
            WarnAbove = 0,
        });
        o.Tile("certificates", T("Zertifikate", "Certificates"), icon: "shield");
        o.Tile("certificateExpiry", T("Nächster Zertifikatsablauf", "Next certificate expiry"), "time", icon: "clock");
        o.Tile(new MatStateItem("certificateErrors", T("Zertifikatsfehler", "Certificate errors"))
        {
            Icon = "x-circle",
            ErrorAbove = 0,
        });
        o.Tile("providers", T("DNS-Anbieter", "DNS providers"), icon: "globe");
        o.Tile("caddy", "Caddy", "status", icon: "server");

        o.Field(new MatField("baseDomain", T("Basisdomain", "Base domain"), "text")
        {
            Max = 253,
            Pattern = HostPattern,
            Placeholder = "example.com",
            Hint = T("Gemeinsame Domain der geschützten Routen: eine Anmeldung gilt für alle Subdomains.",
                "Parent domain of the protected routes: one sign-in works across all its subdomains."),
        });
        o.Field(new MatField("matcadHost", T("Adresse von Matcad", "Matcad host"), "text")
        {
            Max = 253,
            Pattern = HostPattern,
            Placeholder = "matcad.example.com",
            Hint = T("Macht die Oberfläche von Matcad und das Anmeldeportal unter dieser Domain erreichbar. Leer: nur über Port 4433.",
                "Serves the Matcad UI and the login portal under this domain. Empty: only on port 4433."),
        });
        o.Field(new MatField("acmeEmail", T("ACME-E-Mail-Adresse", "ACME e-mail address"), "email")
        {
            Max = 254,
            Placeholder = "admin@example.com",
            Hint = T("Hierhin schickt Let's Encrypt Hinweise zum Ablauf. Leer: anonym registriert, Zertifikate funktionieren trotzdem.",
                "Where Let's Encrypt sends expiry notices. Empty registers anonymously; certificates still work."),
        });
        o.Field(new MatField("portalMode", T("Anmeldeportal", "Login portal"), "select")
        {
            Required = true,
            Options =
            [
                new MatOption("inline", T("Auf der geschützten Domain", "On the protected host")),
                new MatOption("redirect", T("Weiterleitung zu Matcad", "Redirect to Matcad")),
                new MatOption("unauthorized", T("Nur 401, kein Formular", "Plain 401, no form")),
            ],
            Hint = T("Wie eine mit Matcad geschützte Route nicht angemeldete Besucher empfängt.",
                "How a route protected by a Matcad authentication greets visitors who are not signed in."),
        });

        o.LinkAction("open", T("Matcad öffnen", "Open Matcad"), "external", "/");
        o.LinkAction("openRoutes", T("Routen", "Routes"), "route", "/Routes");
        o.LinkAction("openCertificates", T("Zertifikate", "Certificates"), "shield", "/Settings#certs");
        o.PostAction("reloadCaddy", T("Konfiguration neu an Caddy senden", "Push the config to Caddy again"), "refresh");
        o.PostAction("checkCertificates", T("Zertifikate jetzt prüfen", "Check the certificates now"), "check-circle");

        o.Role("admin", T("Administrator", "Administrator"));
        o.Role("user", T("Benutzer", "User"));
    }

    public async Task<IReadOnlyList<MatHealthCheck>> CheckHealthAsync(MatPlatformCall call, CancellationToken ct)
    {
        if (!await auth.CanReadDatabase(ct))
            return [new MatHealthCheck("database", "down", call.T("Die Datenbank kann nicht gelesen werden.", "The database cannot be read."))];

        var checks = new List<MatHealthCheck> { new("database", "ok") };

        // Matcad works without Caddy (it pushes the config when Caddy is back): degraded, never down, so Docker does not restart it.
        if (monitor.CaddyReachable == false)
            checks.Add(new MatHealthCheck("caddy", "degraded", call.T("Die Admin-API von Caddy antwortet nicht.", "Caddy's admin API does not answer.")));
        else if (caddy.LastApply is { Ok: false })
            checks.Add(new MatHealthCheck("caddy", "degraded", call.T("Caddy hat die letzte Konfiguration abgelehnt.", "Caddy refused the last config.")));
        else
            checks.Add(new MatHealthCheck("caddy", "ok"));

        if (await auth.CountAdmins(ct) == 0)
            checks.Add(new MatHealthCheck("administrators", "degraded", call.T("Kein Administrator.", "No administrator.")));

        return checks;
    }

    public async Task<IReadOnlyDictionary<string, MatStateValue>> GetStateAsync(MatPlatformCall call, CancellationToken ct)
    {
        var s = await status.GetAsync(ct);

        string? expiryLevel = null;
        if (s.SoonestExpiry is { } expiry)
        {
            var left = expiry - DateTimeOffset.Now;
            expiryLevel = left < ExpiryError ? "error" : left < ExpiryWarning ? "warn" : "ok";
        }

        return new Dictionary<string, MatStateValue>
        {
            ["routes"] = new(s.Routes, Text: s.RoutesDisabled > 0 ? call.T($"{s.RoutesDisabled} ausgeschaltet", $"{s.RoutesDisabled} disabled") : null),
            ["upstreamErrors"] = new(s.UpstreamErrorHosts, s.UpstreamErrorHosts > 0 ? "warn" : "ok"),
            ["certificates"] = new(s.Certificates),
            ["certificateExpiry"] = new(s.SoonestExpiry, expiryLevel,
                s.CertificatesChecked ? null : call.T("Noch nicht geprüft", "Not checked yet")),
            ["certificateErrors"] = new(s.CertificatesChecked ? s.CertificateErrors : null, s.CertificatesChecked ? (s.CertificateErrors > 0 ? "error" : "ok") : null,
                s.CertificatesChecked ? null : call.T("Noch nicht geprüft", "Not checked yet")),
            ["providers"] = new(s.Providers),
            ["caddy"] = CaddyStatus(s, call),
        };
    }

    private static MatStateValue CaddyStatus(ProxyStatus s, MatPlatformCall call)
    {
        if (s.CaddyReachable == false)
            return new("error", "error", call.T("Admin-API nicht erreichbar", "Admin API not reachable"));
        if (s.LastApply is { Ok: false })
            return new("warn", "warn", call.T("Letzte Konfiguration abgelehnt", "Last config refused"));
        if (s.CaddyReachable == null)
            return new("info", "info", call.T("Noch nicht geprüft", "Not checked yet"));
        return new("ok", "ok", call.T("Läuft", "Running"));
    }

    public Task<IReadOnlyDictionary<string, object?>> GetConfigurationAsync(MatPlatformCall call, CancellationToken ct)
    {
        var s = store.Settings;
        IReadOnlyDictionary<string, object?> values = new Dictionary<string, object?>
        {
            ["baseDomain"] = s.BaseDomain,
            ["matcadHost"] = s.MatcadHost,
            ["acmeEmail"] = s.AcmeEmail,
            ["portalMode"] = PortalModes.Contains(s.PortalMode) ? s.PortalMode : "inline",
        };
        return Task.FromResult(values);
    }

    public async Task<IReadOnlyDictionary<string, string>> ConfigureAsync(IReadOnlyDictionary<string, JsonElement> values, MatPlatformCall call, CancellationToken ct)
    {
        // MatPlatform has checked the values against the definition (type, length, pattern, options); here they are applied like the settings page does.
        var errors = new Dictionary<string, string>();
        var changes = new Dictionary<string, string>();
        foreach (var (key, value) in values)
        {
            var text = value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : value.ValueKind == JsonValueKind.Null ? "" : null;
            if (key is not ("baseDomain" or "matcadHost" or "acmeEmail" or "portalMode"))
                errors[key] = call.T("Diese Einstellung gibt es nicht.", "There is no such setting.");
            else if (text == null)
                errors[key] = call.T("Bitte einen Text angeben.", "Send a text.");
            else if (key == "portalMode" && !PortalModes.Contains(text))
                errors[key] = call.T("Bitte einen der angebotenen Werte wählen.", "Choose one of the offered values.");
            else
                changes[key] = key is "baseDomain" or "matcadHost" ? text.ToLowerInvariant() : text;
        }

        // Nothing is saved when one value is wrong.
        if (errors.Count > 0) return errors;

        var s = store.Settings;
        foreach (var (key, text) in changes)
        {
            switch (key)
            {
                case "baseDomain": s.BaseDomain = text; break;
                case "matcadHost": s.MatcadHost = text; break;
                case "acmeEmail": s.AcmeEmail = text; break;
                case "portalMode": s.PortalMode = text; break;
            }
        }

        store.SaveSettings(s);
        log.LogInformation("Settings {Keys} changed through the platform API by {Caller}", string.Join(", ", values.Keys), call.Caller);
        var (ok, error) = await caddy.ApplyAsync(ct);
        if (!ok) log.LogWarning("Caddy did not take the config after the platform change: {Error}", error);
        return errors;
    }

    public async Task<MatActionResult?> RunActionAsync(string id, MatPlatformCall call, CancellationToken ct)
    {
        switch (id)
        {
            case "reloadCaddy":
                var (ok, error) = await caddy.ApplyAsync(ct);
                log.LogInformation("Config pushed to Caddy through the platform API by {Caller}: {Ok}", call.Caller, ok);
                return ok
                    ? new MatActionResult(true, call.T("Caddy hat die Konfiguration übernommen.", "Caddy took the config."))
                    : new MatActionResult(false, call.T("Caddy hat die Konfiguration nicht übernommen: ", "Caddy did not take the config: ") + Shorten(error));
            case "checkCertificates":
                return monitor.RequestCertificateCheck()
                    ? new MatActionResult(true, call.T("Gestartet", "Started"))
                    : new MatActionResult(false, call.T("Die Zertifikate werden gerade geprüft.", "The certificates are being checked already."));
            default:
                return null;
        }
    }

    private static string Shorten(string? error) =>
        string.IsNullOrEmpty(error) ? "?" : error.Length > 200 ? error[..200] + "…" : error;
}
