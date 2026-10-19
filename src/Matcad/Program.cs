using Matcad.Api;
using Matcad.Auth;
using Matcad.Config;
using Matcad.Controls;
using Matcad.Data;
using Matcad.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Matcad.Platform;
using MatPlatform;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

// Docker HEALTHCHECK: the image has no curl, so the app asks its own /api/health and exits with 0 (healthy) or 1.
if (args.Contains("--healthcheck"))
    return await HealthCheck.RunAsync();

var builder = WebApplication.CreateBuilder(args);

// --- Data volume layout -----------------------------------------------------
// /data/matcad.db  -> SQLite (users, sessions, request log)   /data/config/*.json -> configuration
var dataDir = builder.Configuration["Matcad:DataDir"] ?? "/data";
Directory.CreateDirectory(dataDir);
var dbPath = Path.Combine(dataDir, "matcad.db");

// --- Services ---------------------------------------------------------------
builder.Services.AddRazorPages(o =>
{
    o.Conventions.AllowAnonymousToPage("/Login");
    o.Conventions.AllowAnonymousToPage("/Logout");
    o.Conventions.AllowAnonymousToPage("/Error");
    o.Conventions.AllowAnonymousToPage("/StatusCode");
    // Forward-auth endpoints must be reachable without an admin session.
    o.Conventions.AllowAnonymousToPage("/Auth/Verify");
    o.Conventions.AllowAnonymousToPage("/Auth/Portal");
    // Settings, user management, DNS provider credentials and the setup wizard are admin-only.
    o.Conventions.AuthorizeFolder("/Settings", "Admin");
    o.Conventions.AuthorizeFolder("/Providers", "Admin");
    o.Conventions.AuthorizePage("/Setup", "Admin");
});
builder.Services.AddDbContext<MatcadDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ConfigStore>();
builder.Services.AddSingleton<Toolkit>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddSingleton<LoginAttemptTracker>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<ForwardAuthTokens>();
builder.Services.AddSingleton<CaddyfileAdapter>();
builder.Services.AddSingleton<CaddyfileImporter>();
builder.Services.AddSingleton<DockerRouteCache>();
builder.Services.AddSingleton<RouteProvider>();
builder.Services.AddSingleton<CaddyConfigGenerator>();
builder.Services.AddSingleton<CaddyService>();
builder.Services.AddSingleton<DnsCredentialTester>();
builder.Services.AddSingleton<LogBroadcaster>();
builder.Services.AddHostedService<LogIngestService>();
builder.Services.AddSingleton<DockerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DockerService>());
builder.Services.AddSingleton<ProxyMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProxyMonitor>());
builder.Services.AddScoped<ProxyStatusService>();
builder.Services.AddScoped<RequestLogQueries>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<AuditLog>();

// --- Platform contract of the Mat apps (MatDesign docs/PLATFORM.md) ------------
// /api, /api/health, /api/state, /api/configure, /api/actions from the package MatPlatform (a copy in MatPlatform/); MatcadPlatform describes
// Matcad and supplies the values. The engine API /api/v1 (Matcad__ApiKey) stays as it is. Mattiverse is one caller with one key: when
// MAT_API_KEY is not set, the platform endpoints take the engine key, so an install that only has Matcad__ApiKey needs no second one.
if (string.IsNullOrWhiteSpace(builder.Configuration[MatPlatformSettings.ApiKeyVariable])
    && !string.IsNullOrWhiteSpace(builder.Configuration["Matcad:ApiKey"]))
{
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        [MatPlatformSettings.ApiKeyVariable] = builder.Configuration["Matcad:ApiKey"]
    });
}
var platform = builder.AddMatPlatform<MatcadPlatform>(MatcadPlatform.Describe, o => o.DefaultLanguage = "en");

// Persist Data Protection keys on the volume so antiforgery tokens and the forward-auth
// cookies stay valid across container restarts.
var keysDir = Path.Combine(dataDir, "keys");
Directory.CreateDirectory(keysDir);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysDir))
    .SetApplicationName("Matcad");

builder.Services
    .AddAuthentication(SessionAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(
        SessionAuthenticationHandler.SchemeName, _ => { })
    // Sign-in through MatIdentity, only with MATIDENTITY_AUTHORITY (nothing changes without it). /Login?local=1 stays the way in.
    .AddMatIdentitySignIn<MatcadIdentityUsers>(platform, o =>
    {
        o.CookieScheme = SessionAuthenticationHandler.SchemeName;
        o.LoginPath = "/Login";
        o.AccessDeniedPath = "/Login?local=1&denied=1";
    });

if (platform.IdentityEnabled)
{
    // Matcad is often the proxy in front of MatIdentity itself: when MatIdentity cannot be reached, the sign-in page
    // falls back to the local form after a short wait instead of the default minute.
    builder.Services.Configure<OpenIdConnectOptions>(MatIdentitySignIn.Scheme, o => o.BackchannelTimeout = TimeSpan.FromSeconds(10));
}

builder.Services.AddAuthorization(o =>
{
    // Every page requires a logged-in user unless it opts out via AllowAnonymous.
    o.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
    o.AddPolicy("Admin", p => p.RequireRole(nameof(UserRole.Admin)));
});

// Caddy and Matcad share a private Docker network: believe the client address Caddy forwards, but only from
// there. Anyone reaching port 4433 directly from elsewhere cannot make up an address to dodge the login limits.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
    foreach (var network in new[] { "127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "100.64.0.0/10", "fc00::/7" })
        o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
});

// Listen on 4433 inside the container (mapped 1:1 by compose); Matcad__Port changes it.
builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(builder.Configuration.GetValue("Matcad:Port", 4433)));

var app = builder.Build();

// --- Database bootstrap + seed ---------------------------------------------
using (var scope = app.Services.CreateScope())
{
    await DatabaseBootstrapper.InitializeAsync(scope.ServiceProvider.GetRequiredService<MatcadDbContext>(), app.Logger);
    // MATCAD_ADMIN_PASSWORD = headless install (e.g. by Mattiverse): the admin gets that password, without the forced change.
    var seededFromEnv = await scope.ServiceProvider.GetRequiredService<AuthService>()
        .EnsureSeedAdmin(app.Logger, builder.Configuration["MATCAD_ADMIN_PASSWORD"]);

    // Fresh installs start EMPTY and run the first-run setup wizard. Example data
    // is only loaded on demand (Settings -> Import / Export). Upgraded installs
    // that already carry configuration are marked "set up" so the wizard never
    // interrupts them. So is a headless install: the wizard's first step only
    // exists to replace admin/admin.
    var store = scope.ServiceProvider.GetRequiredService<ConfigStore>();
    var settings = store.Settings;
    if (seededFromEnv && !settings.SetupCompleted)
    {
        settings.SetupCompleted = true;
        store.SaveSettings(settings);
        app.Logger.LogInformation("MATCAD_ADMIN_PASSWORD set - setup wizard skipped.");
    }
    if (!settings.SetupCompleted &&
        (store.Providers.Any() || store.Routes.Any() || store.Authentications.Any()
         || !string.IsNullOrWhiteSpace(settings.BaseDomain)))
    {
        settings.SetupCompleted = true;
        store.SaveSettings(settings);
        app.Logger.LogInformation("Existing configuration detected - setup wizard skipped.");
    }
}

// From here on every change of the configuration goes into the audit log (the start-up adjustments above are not changes of a person).
app.Services.GetRequiredService<AuditLog>().Attach(app.Services.GetRequiredService<ConfigStore>());

// Push the current desired config to Caddy on startup (best effort; Caddy may
// still be starting). Route changes re-push at runtime.
_ = Task.Run(async () =>
{
    var caddy = app.Services.GetRequiredService<CaddyService>();
    for (var attempt = 0; attempt < 10; attempt++)
    {
        var (ok, _) = await caddy.ApplyAsync();
        if (ok) break;
        await Task.Delay(TimeSpan.FromSeconds(3));
    }
});

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error");

app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    await next();
});

var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".webmanifest"] = "application/manifest+json";
// CSS and JS are linked with ?v=<file hash> (asp-append-version): a changed file has a new address, so the old one may be kept for good.
// (The service worker script must always be re-checked.)
void KeepVersioned(StaticFileResponseContext context)
{
    if (context.Context.Request.Query.ContainsKey("v") && context.File.Name != "sw.js")
        context.Context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
}

app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypes, OnPrepareResponse = KeepVersioned });

// The login portal is shown on the protected host, where Caddy proxies only /auth/* to Matcad: the toolkit, the styles and the logo it needs are served there as well.
foreach (var (folder, path) in new[] { ("matui", "/auth/ui"), ("css", "/auth/css"), ("img", "/auth/img") })
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.Combine(app.Environment.WebRootPath, folder)),
        RequestPath = path,
        ContentTypeProvider = contentTypes,
        OnPrepareResponse = KeepVersioned
    });
}
// A 404 or 403 shows a page of the app (with the original status code) instead of an empty response.
app.UseStatusCodePagesWithReExecute("/StatusCode/{0}");
app.UseRouting();
// The platform endpoints of MatPlatform limit their callers (AddMatPlatform registers the policies).
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<AccountGuardMiddleware>();

app.MapRazorPages();

// Reachability probe for the container health check, orchestrators (Mattiverse) and the browser: the offline page
// and the connection banner ask it to find out whether Matcad is back. Anonymous on purpose; reveals nothing.
app.MapMethods("/healthz", ["GET", "HEAD"], (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Text("ok");
}).AllowAnonymous();

// Real-time log stream (Server-Sent Events). Dependency-free live updates.
app.MapGet("/logs/stream", async (HttpContext ctx, LogBroadcaster broadcaster, CancellationToken ct) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";

    var jsonOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
    async Task Send(LogEntry entry)
    {
        await ctx.Response.WriteAsync($"data: {System.Text.Json.JsonSerializer.Serialize(entry, jsonOptions)}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    // Send the headers right away: the browser reports "connected" when they arrive, not when the first entry does.
    await ctx.Response.StartAsync(ct);
    await ctx.Response.Body.FlushAsync(ct);

    var (reader, lease) = broadcaster.Subscribe();
    using (lease)
    {
        try
        {
            // Oldest first: the page puts every line on top of the list.
            foreach (var entry in broadcaster.Recent().Reverse()) await Send(entry);
            while (!ct.IsCancellationRequested)
            {
                // A comment line now and then keeps idle connections open through proxies, and shows a dead client.
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                heartbeat.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    if (!await reader.WaitToReadAsync(heartbeat.Token)) break;
                    while (reader.TryRead(out var entry)) await Send(entry);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await ctx.Response.WriteAsync(": keep-alive\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}).RequireAuthorization();

// Machine-to-machine REST API (used by matOS, Mattiverse and the MatCMS Cloud). Guarded by the X-Api-Key header.
app.MapMatcadApi();

// The platform contract next to it (no route shares a path with /api/v1).
app.MapMatPlatform();

app.Run();
return 0;

// Lets the integration tests start the app (WebApplicationFactory<Program>).
public partial class Program;
