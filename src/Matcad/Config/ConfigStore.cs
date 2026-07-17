using System.Text.Json;

namespace Matcad.Config;

/// <summary>
/// Loads and persists the JSON configuration files on the data volume.
/// Thread-safe and cached in memory; every save rewrites the whole file
/// (config volumes are small). Providers/Routes/Authentications live in their
/// own files; settings in settings.json.
/// </summary>
public class ConfigStore
{
    private readonly string _dir;
    private readonly ILogger<ConfigStore> _log;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private List<ProviderConfig>? _providers;
    private List<RouteConfig>? _routes;
    private List<AuthenticationConfig>? _authentications;
    private MatcadSettings? _settings;

    public ConfigStore(IConfiguration cfg, ILogger<ConfigStore> log)
    {
        _log = log;
        _dir = cfg["Matcad:ConfigDir"] ?? "/data/config";
        Directory.CreateDirectory(_dir);
    }

    private string Path(string name) => System.IO.Path.Combine(_dir, name);

    private List<T> Load<T>(string file)
    {
        var path = Path(file);
        if (!File.Exists(path)) return new List<T>();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<T>>(json, JsonOpts) ?? new List<T>();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to read config {File}", file);
            return new List<T>();
        }
    }

    private void Save<T>(string file, List<T> items)
    {
        var path = Path(file);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(items, JsonOpts));
        File.Move(tmp, path, overwrite: true);
    }

    public List<ProviderConfig> Providers
    {
        get { lock (_lock) { return _providers ??= Load<ProviderConfig>("providers.json"); } }
    }

    public List<RouteConfig> Routes
    {
        get { lock (_lock) { return _routes ??= Load<RouteConfig>("routes.json"); } }
    }

    public List<AuthenticationConfig> Authentications
    {
        get { lock (_lock) { return _authentications ??= Load<AuthenticationConfig>("authentications.json"); } }
    }

    public MatcadSettings Settings
    {
        get
        {
            lock (_lock)
            {
                if (_settings != null) return _settings;
                var path = Path("settings.json");
                if (File.Exists(path))
                {
                    try { _settings = JsonSerializer.Deserialize<MatcadSettings>(File.ReadAllText(path), JsonOpts); }
                    catch (Exception ex) { _log.LogError(ex, "Failed to read settings.json"); }
                }
                return _settings ??= new MatcadSettings();
            }
        }
    }

    // --- Mutations: assign Id, stamp audit fields, persist. ---

    private static long NextId<T>(IEnumerable<T> items) where T : ConfigEntity
        => items.Any() ? items.Max(x => x.Id) + 1 : 1;

    public void SaveProviders() { lock (_lock) { Save("providers.json", Providers); } }
    public void SaveRoutes() { lock (_lock) { Save("routes.json", Routes); } }
    public void SaveAuthentications() { lock (_lock) { Save("authentications.json", Authentications); } }

    public void SaveSettings(MatcadSettings settings)
    {
        lock (_lock)
        {
            _settings = settings;
            File.WriteAllText(Path("settings.json"), JsonSerializer.Serialize(settings, JsonOpts));
        }
    }

    public void UpsertProvider(ProviderConfig p, long? userId) => Upsert(Providers, p, userId, SaveProviders);
    public void UpsertRoute(RouteConfig r, long? userId) => Upsert(Routes, r, userId, SaveRoutes);
    public void UpsertAuthentication(AuthenticationConfig a, long? userId) => Upsert(Authentications, a, userId, SaveAuthentications);

    private void Upsert<T>(List<T> list, T item, long? userId, Action save) where T : ConfigEntity
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (item.Id == 0)
            {
                item.Id = NextId(list);
                item.CreateDate = now;
                item.CreateUserId = userId;
                list.Add(item);
            }
            else
            {
                var idx = list.FindIndex(x => x.Id == item.Id);
                item.UpdateDate = now;
                item.UpdateUserId = userId;
                if (idx >= 0)
                {
                    item.CreateDate = list[idx].CreateDate;
                    item.CreateUserId = list[idx].CreateUserId;
                    list[idx] = item;
                }
                else list.Add(item);
            }
            save();
        }
    }

    public void DeleteProvider(long id) { lock (_lock) { Providers.RemoveAll(x => x.Id == id); SaveProviders(); } }
    public void DeleteAuthentication(long id) { lock (_lock) { Authentications.RemoveAll(x => x.Id == id); SaveAuthentications(); } }

    public void DeleteRoute(long id)
    {
        // The route tree is derived from host names, so no re-parenting is needed.
        lock (_lock) { Routes.RemoveAll(x => x.Id == id); SaveRoutes(); }
    }
}
