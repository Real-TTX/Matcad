using Matcat.Auth;
using Matcat.Config;
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
});
builder.Services.AddDbContext<MatcatDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddSingleton<ConfigStore>();
builder.Services.AddScoped<AuthService>();

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

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

app.Run();
