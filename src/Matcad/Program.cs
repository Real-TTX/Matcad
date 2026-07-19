using Matcad.Auth;
using Matcad.Config;
using Microsoft.AspNetCore.DataProtection;
using Matcad.Data;
using Matcad.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

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
    // Settings and user management are admin-only.
    o.Conventions.AuthorizeFolder("/Settings", "Admin");
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
        SessionAuthenticationHandler.SchemeName, _ => { });

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
    // WAL improves concurrent read/write throughput for the high-volume request log.
    db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
    var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
    await auth.EnsureSeedAdmin(app.Logger);

    // On the very first start only, seed illustrative example data. Gated by a
    // one-time marker file (NOT by "config is empty") so clearing everything and
    // restarting never resurrects the examples or overwrites settings.
    var store = scope.ServiceProvider.GetRequiredService<ConfigStore>();
    var seedMarker = Path.Combine(dataDir, ".example-seeded");
    if (!File.Exists(seedMarker))
    {
        if (Matcad.Config.ExampleData.IsEmpty(store))
        {
            var admin = await auth.FindUser("admin");
            Matcad.Config.ExampleData.Seed(store, admin?.Id);
            if (await auth.FindUser("demo") == null)
                await auth.CreateUser("demo", "demo", UserRole.User, admin?.Id);
            app.Logger.LogInformation("Seeded example data (disabled example routes).");
        }
        await File.WriteAllTextAsync(seedMarker, DateTime.UtcNow.ToString("o"));
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
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

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

app.Run();
