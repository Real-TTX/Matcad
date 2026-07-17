using Matcat.Config;
using Matcat.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// --- Data volume layout -----------------------------------------------------
// /data/matcat.db  -> SQLite (logic)     /data/config/*.json -> configs
var dataDir = builder.Configuration["Matcat:DataDir"] ?? "/data";
Directory.CreateDirectory(dataDir);
var dbPath = Path.Combine(dataDir, "matcat.db");

// --- Services ---------------------------------------------------------------
builder.Services.AddRazorPages();
builder.Services.AddDbContext<MatcatDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddSingleton<ConfigStore>();

// Listen on 4433 inside the container (mapped 1:1 by compose).
builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(4433));

var app = builder.Build();

// --- Database bootstrap -----------------------------------------------------
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MatcatDbContext>();
    db.Database.EnsureCreated();
}

app.UseStaticFiles();
app.UseRouting();
app.MapRazorPages();

app.Run();
