using System.Text.Json;

namespace Matcad.Config;

/// <summary>
/// Loads and persists the JSON configuration files on the data volume.
/// Thread-safe and cached in memory; every save rewrites the whole file
/// (config volumes are small). Providers/Routes/Authentications live in their
/// own files; settings in settings.json.
///
/// The lists are immutable snapshots: a change builds a new list and swaps it in, so a page, the Caddy
/// config generator and the Docker poller can read at the same time without seeing a half-done change.
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

    private IReadOnlyList<ProviderConfig>? _providers;
    private IReadOnlyList<RouteConfig>? _routes;
    private IReadOnlyList<AuthenticationConfig>? _authentications;
    private MatcadSettings? _settings;
    private Dictionary<string, long>? _sequences;

    public ConfigStore(IConfiguration cfg, ILogger<ConfigStore> log)
    {
        _log = log;
        _dir = cfg["Matcad:ConfigDir"] ?? "/data/config";
        Directory.CreateDirectory(_dir);
    }

    private string Path(string name) => System.IO.Path.Combine(_dir, name);

    public IReadOnlyList<ProviderConfig> Providers { get { lock (_lock) return _providers ??= Load<ProviderConfig>("providers.json"); } }
    public IReadOnlyList<RouteConfig> Routes { get { lock (_lock) return _routes ??= Load<RouteConfig>("routes.json"); } }
    public IReadOnlyList<AuthenticationConfig> Authentications { get { lock (_lock) return _authentications ??= Load<AuthenticationConfig>("authentications.json"); } }

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

    // --- Reading and writing the files ---

    private List<T> Load<T>(string file)
    {
        var path = Path(file);
        if (!File.Exists(path)) return new List<T>();
        try
        {
            return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), JsonOpts) ?? new List<T>();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to read config {File}", file);
            return new List<T>();
        }
    }

    /// <summary>Writes next to the target first and then swaps, so a crash never leaves a half-written file.</summary>
    private void Write<T>(string file, T content)
    {
        var path = Path(file);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(content, JsonOpts));
        File.Move(temp, path, overwrite: true);
    }

    public void SaveSettings(MatcadSettings settings)
    {
        lock (_lock)
        {
            Write("settings.json", settings);
            _settings = settings;
        }
    }

    // --- Mutations: assign Id, stamp audit fields, persist. ---

    public void UpsertProvider(ProviderConfig p, long? userId) => Upsert("providers", "providers.json", ref _providers, p, userId);
    public void UpsertRoute(RouteConfig r, long? userId) => Upsert("routes", "routes.json", ref _routes, r, userId);

    /// <summary>
    /// Saves a route unless another route already has its host (or, for a port binding, its port). With
    /// <paramref name="updateSameTarget"/> and no id the route that has it is updated instead - "make sure this route exists".
    /// Returns false when the host or port is taken by another route.
    /// </summary>
    public bool TryUpsertRoute(RouteConfig route, long? userId, bool updateSameTarget)
    {
        lock (_lock)
        {
            var other = RouteWithSameTarget(route);
            if (other != null)
            {
                if (!updateSameTarget || route.Id != 0) return false;
                route.Id = other.Id;
            }
            UpsertRoute(route, userId);
            return true;
        }
    }

    // Another route with the same host (or, for a port binding, the same port).
    private RouteConfig? RouteWithSameTarget(RouteConfig route) =>
        Routes.FirstOrDefault(r => r.Id != route.Id && (route.ListenPort is > 0
            ? r.ListenPort == route.ListenPort
            : r.ListenPort is not > 0 && string.Equals(r.Host, route.Host, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// The stored route that <see cref="TryUpsertRoute"/> with <c>updateSameTarget</c> would replace: the one with the route's id,
    /// or - for a route without an id - the one that has its host or port. Null when the save would create a new route.
    /// </summary>
    public RouteConfig? FindRouteToReplace(RouteConfig route)
    {
        lock (_lock)
            return route.Id != 0 ? Routes.FirstOrDefault(r => r.Id == route.Id) : RouteWithSameTarget(route);
    }

    public void UpsertAuthentication(AuthenticationConfig a, long? userId) => Upsert("authentications", "authentications.json", ref _authentications, a, userId);

    public void DeleteProvider(long id) => Remove("providers.json", ref _providers, id);
    public void DeleteAuthentication(long id) => Remove("authentications.json", ref _authentications, id);
    // The route tree is derived from host names, so no re-parenting is needed.
    public void DeleteRoute(long id) => Remove("routes.json", ref _routes, id);

    private void Upsert<T>(string key, string file, ref IReadOnlyList<T>? field, T item, long? userId) where T : ConfigEntity
    {
        lock (_lock)
        {
            var list = new List<T>(field ??= Load<T>(file));
            var now = DateTime.UtcNow;
            if (item.Id == 0)
            {
                item.Id = NextId(key, list);
                item.CreateDate = now;
                item.CreateUserId = userId;
                list.Add(item);
            }
            else
            {
                var index = list.FindIndex(x => x.Id == item.Id);
                item.UpdateDate = now;
                item.UpdateUserId = userId;
                if (index >= 0)
                {
                    item.CreateDate = list[index].CreateDate;
                    item.CreateUserId = list[index].CreateUserId;
                    list[index] = item;
                }
                else
                {
                    // An id the caller chose (the REST API allows it): the item is new.
                    item.CreateDate = now;
                    item.CreateUserId = userId;
                    list.Add(item);
                }
            }
            Write(file, list);
            field = list;
        }
    }

    private void Remove<T>(string file, ref IReadOnlyList<T>? field, long id) where T : ConfigEntity
    {
        lock (_lock)
        {
            var list = new List<T>(field ??= Load<T>(file));
            list.RemoveAll(x => x.Id == id);
            Write(file, list);
            field = list;
        }
    }

    // Monotonic ID sequences per entity type, persisted so IDs are never reused
    // (reusing a deleted top-ID would silently re-point references to a new item).
    private Dictionary<string, long> Sequences => _sequences ??= LoadSequences();

    private Dictionary<string, long> LoadSequences()
    {
        var path = Path("sequences.json");
        if (File.Exists(path))
        {
            try { return JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(path), JsonOpts) ?? new(); }
            catch (Exception ex) { _log.LogError(ex, "Failed to read sequences.json"); }
        }
        return new();
    }

    private long NextId<T>(string key, IEnumerable<T> items) where T : ConfigEntity
    {
        var current = items.Any() ? items.Max(x => x.Id) : 0;
        var highWater = Sequences.TryGetValue(key, out var v) ? v : 0;
        var next = Math.Max(current, highWater) + 1;
        Sequences[key] = next;
        Write("sequences.json", Sequences);
        return next;
    }

    /// <summary>Snapshot of the ID sequences (for backup).</summary>
    public Dictionary<string, long> SequenceSnapshot() { lock (_lock) return new Dictionary<string, long>(Sequences); }

    /// <summary>Replaces all config from a backup and persists it.</summary>
    public void ImportConfig(MatcadSettings settings, List<ProviderConfig> providers,
        List<RouteConfig> routes, List<AuthenticationConfig> authentications, Dictionary<string, long>? sequences)
    {
        lock (_lock)
        {
            Write("settings.json", settings);
            _settings = settings;
            Write("providers.json", providers);
            _providers = providers;
            Write("routes.json", routes);
            _routes = routes;
            Write("authentications.json", authentications);
            _authentications = authentications;
            if (sequences != null)
            {
                Write("sequences.json", sequences);
                _sequences = sequences;
            }
        }
    }
}
