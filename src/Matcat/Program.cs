using Matcat.Auth;
using Matcat.Config;
using Microsoft.AspNetCore.DataProtection;
using Matcat.Data;
using Matcat.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// --- Data volume layout -----------------------------------------------------
// /data/matcat.db  -> SQLite (logic)     /data/config/*.json -> configs
var dataDir = builder.Configuration["Matcat:DataDir"] ?? "/data";
Directory.CreateDirectory(dataDir);
var dbPath = Path.Combine(dataDir, "matcat.db");

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
builder.Services.AddDbContext<MatcatDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddSingleton<ConfigStore>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<CaddyConfigGenerator>();
builder.Services.AddSingleton<CaddyService>();
builder.Services.AddSingleton<LogBroadcaster>();
builder.Services.AddHostedService<LogIngestService>();

// Persist Data Protection keys on the volume so antiforgery tokens and any
// protected payloads stay valid across container restarts.
var keysDir = Path.Combine(dataDir, "keys");
Directory.CreateDirectory(keysDir);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysDir))
    .SetApplicationName("Matcat");

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
    var db = scope.ServiceProvider.GetRequiredService<MatcatDbContext>();
    db.Database.EnsureCreated();
    var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
    await auth.EnsureSeedAdmin(app.Logger);
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
