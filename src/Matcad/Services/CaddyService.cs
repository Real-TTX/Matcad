using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Matcad.Config;

namespace Matcad.Services;

/// <summary>
/// Talks to the Caddy admin API. Regenerates the full config from the current
/// Matcad state and pushes it via POST /load. Call <see cref="ApplyAsync"/>
/// whenever routes/providers/authentications change and once on startup.
/// </summary>
public class CaddyService
{
    private readonly IHttpClientFactory _http;
    private readonly CaddyConfigGenerator _generator;
    private readonly ConfigStore _store;
    private readonly IConfiguration _cfg;
    private readonly ILogger<CaddyService> _log;

    public CaddyService(IHttpClientFactory http, CaddyConfigGenerator generator,
        ConfigStore store, IConfiguration cfg, ILogger<CaddyService> log)
    {
        _http = http;
        _generator = generator;
        _store = store;
        _cfg = cfg;
        _log = log;
    }

    private string AdminUrl
    {
        get
        {
            var fromSettings = _store.Settings.CaddyAdminUrl;
            if (!string.IsNullOrWhiteSpace(fromSettings)) return fromSettings.TrimEnd('/');
            return (_cfg["Matcad:Caddy:AdminUrl"] ?? "http://caddy:2019").TrimEnd('/');
        }
    }

    public string BuildJson() => BuildNode().ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    /// <summary>The generated config with the optional raw-passthrough JSON deep-merged in.</summary>
    private JsonNode BuildNode()
    {
        var node = JsonSerializer.SerializeToNode(_generator.Build()) ?? new JsonObject();
        var raw = _store.Settings.RawCaddyJson;
        if (!string.IsNullOrWhiteSpace(raw) && node is JsonObject target)
        {
            try
            {
                if (JsonNode.Parse(raw) is JsonObject source) DeepMerge(target, source);
            }
            catch (Exception ex) { _log.LogWarning(ex, "Invalid raw Caddy JSON; ignoring it"); }
        }
        return node;
    }

    // Objects merge recursively; arrays concatenate; scalars are overwritten.
    private static void DeepMerge(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            if (target[key] is JsonObject to && value is JsonObject so) DeepMerge(to, so);
            else if (target[key] is JsonArray ta && value is JsonArray sa)
                foreach (var item in sa) ta.Add(item?.DeepClone());
            else target[key] = value?.DeepClone();
        }
    }

    /// <summary>Regenerates and pushes the config. Returns (success, error?).</summary>
    public async Task<(bool Ok, string? Error)> ApplyAsync(CancellationToken ct = default)
    {
        var json = BuildJson();
        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await client.PostAsync($"{AdminUrl}/load", content, ct);
            if (resp.IsSuccessStatusCode)
            {
                _log.LogInformation("Pushed config to Caddy ({Routes} routes).", _store.Routes.Count);
                return (true, null);
            }
            var body = await resp.Content.ReadAsStringAsync(ct);
            _log.LogError("Caddy rejected config ({Status}): {Body}", resp.StatusCode, body);
            return (false, $"{(int)resp.StatusCode}: {body}");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to reach Caddy admin API at {Url}", AdminUrl);
            return (false, ex.Message);
        }
    }

    public async Task<string?> GetRunningConfigAsync(CancellationToken ct = default)
    {
        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            return await client.GetStringAsync($"{AdminUrl}/config/", ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read running Caddy config.");
            return null;
        }
    }
}
