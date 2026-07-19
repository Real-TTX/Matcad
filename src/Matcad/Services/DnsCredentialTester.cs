using System.Net.Http.Json;
using System.Text.Json;

namespace Matcad.Services;

/// <summary>
/// Optional credential check for DNS providers. Where a provider's API offers a
/// cheap auth call (touching no DNS records), this verifies the stored
/// credentials directly - so the user learns they're wrong immediately instead
/// of waiting for an ACME DNS-01 challenge to time out. Providers without a
/// tester return Ok = null ("no test available").
/// </summary>
public class DnsCredentialTester
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<DnsCredentialTester> _log;
    public DnsCredentialTester(IHttpClientFactory http, ILogger<DnsCredentialTester> log)
    { _http = http; _log = log; }

    /// <summary>Ok = true (valid) / false (invalid) / null (no test for this type).</summary>
    public Task<(bool? Ok, string Message)> TestAsync(string type, IDictionary<string, string> creds, CancellationToken ct = default) =>
        (type ?? "").Trim().ToLowerInvariant() switch
        {
            "netcup" => TestNetcupAsync(creds, ct),
            _ => Task.FromResult<(bool?, string)>(
                (null, $"No credential test available for '{type}' yet - it can only be verified by issuing a certificate."))
        };

    private const string NetcupEndpoint = "https://ccp.netcup.net/run/webservice/servers/endpoint.php?JSON";

    private async Task<(bool?, string)> TestNetcupAsync(IDictionary<string, string> creds, CancellationToken ct)
    {
        string Get(params string[] keys)
        {
            foreach (var k in keys)
                if (creds.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) return v.Trim();
            return "";
        }
        var cn = Get("customer_number", "customernumber");
        var key = Get("api_key", "apikey");
        var pw = Get("api_password", "apipassword");
        if (cn.Length == 0 || key.Length == 0 || pw.Length == 0)
            return (false, "Missing customer_number / api_key / api_password.");

        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        try
        {
            // "login" validates customer number + API key + API password without
            // touching any DNS records.
            var loginReq = new { action = "login", param = new { customernumber = cn, apikey = key, apipassword = pw } };
            using var resp = await client.PostAsJsonAsync(NetcupEndpoint, loginReq, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
            var msg = root.TryGetProperty("longmessage", out var lm) ? lm.GetString()
                : root.TryGetProperty("shortmessage", out var sm) ? sm.GetString() : "";

            if (string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
            {
                // Be tidy: log out the session we just opened.
                var sid = root.TryGetProperty("responsedata", out var rd) && rd.TryGetProperty("apisessionid", out var idEl)
                    ? idEl.GetString() : null;
                if (!string.IsNullOrEmpty(sid))
                {
                    var logoutReq = new { action = "logout", param = new { customernumber = cn, apikey = key, apisessionid = sid } };
                    try { (await client.PostAsJsonAsync(NetcupEndpoint, logoutReq, ct)).Dispose(); } catch { /* best effort */ }
                }
                return (true, "netcup API login succeeded - the credentials are valid.");
            }
            return (false, $"netcup rejected the credentials: {msg}");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "netcup credential test failed");
            return (false, "Could not reach the netcup API: " + ex.Message);
        }
    }
}
