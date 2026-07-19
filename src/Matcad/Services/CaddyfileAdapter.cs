using System.Diagnostics;

namespace Matcad.Services;

/// <summary>
/// Converts a Caddyfile to Caddy JSON using Caddy's own adapter
/// (<c>caddy adapt</c>, run as a subprocess against the bundled static binary).
/// This is only an adaptation - it never loads/replaces the running config.
/// </summary>
public class CaddyfileAdapter
{
    private readonly ILogger<CaddyfileAdapter> _log;
    public CaddyfileAdapter(ILogger<CaddyfileAdapter> log) => _log = log;

    public record Result(string? Json, string? Error, string? Warnings);

    public async Task<Result> AdaptAsync(string caddyfile, CancellationToken ct = default)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"matcad-import-{Guid.NewGuid():N}.caddy");
        await File.WriteAllTextAsync(tmp, caddyfile, ct);
        try
        {
            var psi = new ProcessStartInfo("caddy")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("adapt");
            psi.ArgumentList.Add("--config");
            psi.ArgumentList.Add(tmp);
            psi.ArgumentList.Add("--adapter");
            psi.ArgumentList.Add("caddyfile");

            using var p = Process.Start(psi);
            if (p == null) return new Result(null, "Could not start the caddy adapter.", null);

            var stdout = await p.StandardOutput.ReadToEndAsync(ct);
            var stderr = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);

            if (p.ExitCode != 0)
                return new Result(null, string.IsNullOrWhiteSpace(stderr) ? "Adaptation failed." : stderr.Trim(), null);

            return new Result(stdout, null, string.IsNullOrWhiteSpace(stderr) ? null : stderr.Trim());
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "caddy adapt failed");
            return new Result(null, ex.Message, null);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
    }
}
