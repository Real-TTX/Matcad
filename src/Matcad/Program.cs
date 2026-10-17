using Matcad.Auth;
using Matcad.Config;
using Microsoft.AspNetCore.DataProtection;
using Matcad.Data;
using Matcad.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Matcad.Platform;
using MatPlatform;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

// Docker HEALTHCHECK: the image has no curl, so the app asks its own /api/health and exits with 0 (healthy) or 1.
if (args.Contains("--healthcheck"))
    return await HealthCheck.RunAsync();

var builder = WebApplication.CreateBuilder(args);

// --- Data volume layout -----------------------------------------------------
// /data/matcad.db  -> SQLite (logic)     /data/config/*.json -> configs
var dataDir = builder.Configuration["Matcad:DataDir"] ?? "/data";
Directory.CreateDirectory(dataDir);
var dbPath = Path.Combine(dataDir, "matcad.db");

// --- Services ---------------------------------------------------------------
builder.Services.AddRazorPages(o =>
{
    o.Conventions.AllowAnonymousToPage("/Login");
    o.Conventions.AllowAnonymousToPage("/Logout");
    // Forward-auth endpoints must be reachable without an admin session.
    o.Conventions.AllowAnonymousToPage("/Auth/Verify");
    o.Conventions.AllowAnonymousToPage("/Auth/Portal");
    // Settings, user management and the setup wizard are admin-only.
    o.Conventions.AuthorizeFolder("/Settings", "Admin");
    o.Conventions.AuthorizePage("/Setup", "Admin");
});
builder.Services.AddDbContext<MatcadDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddSingleton<ConfigStore>();
builder.Services.AddScoped<AuthService>();
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

// Persist Data Protection keys on the volume so antiforgery tokens and any
// protected payloads stay valid across container restarts.
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

// Listen on 4433 inside the container (mapped 1:1 by compose).
builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(4433));

var app = builder.Build();

// --- Database bootstrap + seed ---------------------------------------------
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MatcadDbContext>();
    db.Database.EnsureCreated();
    // EnsureCreated leaves an existing database alone: columns added later come from here.
    DatabaseUpgrader.Upgrade(db);
    // WAL improves concurrent read/write throughput for the high-volume request log.
    db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
    var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
    // MATCAD_ADMIN_PASSWORD = headless install (e.g. by Mattiverse): the admin gets that password.
    var seededFromEnv = await auth.EnsureSeedAdmin(app.Logger, builder.Configuration["MATCAD_ADMIN_PASSWORD"]);

    // Fresh installs start EMPTY and run the first-run setup wizard. Example data
    // is only loaded on demand (Settings -> Import / Export). Upgraded installs
    // that already carry configuration are marked "set up" so the wizard never
    // interrupts them. So is a headless install: the wizard's first step only
    // exists to replace admin/admin.
    var store = scope.ServiceProvider.GetRequiredService<ConfigStore>();
    var s0 = store.Settings;
    if (seededFromEnv && !s0.SetupCompleted)
    {
        s0.SetupCompleted = true;
        store.SaveSettings(s0);
        app.Logger.LogInformation("MATCAD_ADMIN_PASSWORD set - setup wizard skipped.");
    }
    if (!s0.SetupCompleted &&
        (store.Providers.Any() || store.Routes.Any() || store.Authentications.Any()
         || !string.IsNullOrWhiteSpace(s0.BaseDomain)))
    {
        s0.SetupCompleted = true;
        store.SaveSettings(s0);
        app.Logger.LogInformation("Existing configuration detected - setup wizard skipped.");
    }
}

// Push the current desired config to Caddy on startup (best effort; Caddy may
// still be starting). Route changes re-push at runtime.
_ = Task.Run(async () =>
{
    for (var attempt = 0; attempt < 10; attempt++)
    {
        var caddy = app.Services.GetRequiredService<CaddyService>();
        var (ok, _) = await caddy.ApplyAsync();
        if (ok) break;
        await Task.Delay(TimeSpan.FromSeconds(3));
    }
});

app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// First-run: funnel the logged-in admin into the setup wizard until it's done.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    bool Exempt(string p) => path.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    var exempt = Exempt("/Setup") || Exempt("/Login") || Exempt("/Logout") || Exempt("/Auth/") || Exempt("/healthz")
        || Exempt("/css") || Exempt("/js") || Exempt("/lib") || Exempt("/logs") || Exempt("/favicon")
        || Exempt(MatIdentitySignIn.CallbackPath) || Exempt(MatIdentitySignIn.SignedOutCallbackPath)
        // The platform contract (/api, /api/health, ...) answers JSON, never the setup page; /api/v1 keeps its old behaviour.
        || path.Equals("/api", StringComparison.OrdinalIgnoreCase)
        || (Exempt("/api/") && !Exempt("/api/v1"));
    if (!exempt && ctx.User.Identity?.IsAuthenticated == true && ctx.User.IsInRole(nameof(UserRole.Admin)))
    {
        var store = ctx.RequestServices.GetRequiredService<ConfigStore>();
        if (!store.Settings.SetupCompleted) { ctx.Response.Redirect("/Setup"); return; }
    }
    await next();
});

app.MapRazorPages();

// Liveness probe for container health checks / orchestrators. Anonymous on purpose; reveals nothing.
app.MapMethods("/healthz", new[] { "GET", "HEAD" }, () => Results.Text("ok")).AllowAnonymous();

// Real-time log stream (Server-Sent Events). Dependency-free live updates.
app.MapGet("/logs/stream", async (HttpContext ctx, LogBroadcaster broadcaster, CancellationToken ct) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";

    var jsonOpts = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
    async Task Send(LogEntry e)
    {
        await ctx.Response.WriteAsync($"data: {System.Text.Json.JsonSerializer.Serialize(e, jsonOpts)}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    var (reader, lease) = broadcaster.Subscribe();
    using (lease)
    {
        foreach (var e in broadcaster.Recent().Reverse()) await Send(e);
        try
        {
            await foreach (var e in reader.ReadAllAsync(ct)) await Send(e);
        }
        catch (OperationCanceledException) { }
    }
}).RequireAuthorization();

// Machine-to-machine REST API (used by matOS). Guarded by the X-Api-Key header.
Matcad.Api.MatcadApi.MapMatcadApi(app);

// The platform contract next to it (no route shares a path with /api/v1).
app.MapMatPlatform();

app.Run();
return 0;
