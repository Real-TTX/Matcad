using System.Net;
using System.Text;
using Matcad.Config;
using Matcad.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matcad.Tests;

/// <summary>
/// A Caddy that was started again (its container recreated, as when Mattiverse publishes another port on it, or after a crash) runs the bootstrap Caddyfile of its
/// image and lost Matcad's config: Matcad recognises it by the bootstrap answer and pushes its config again (<see cref="ProxyMonitor"/>).
/// </summary>
public sealed class CaddyRestartTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "matcad-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly IConfiguration _cfg;
    private readonly ConfigStore _store;

    public CaddyRestartTests()
    {
        _cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Matcad:ConfigDir"] = _folder }).Build();
        _store = new ConfigStore(_cfg, NullLogger<ConfigStore>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { /* temp folder */ }
    }

    [Fact]
    public async Task A_Caddy_that_runs_the_bootstrap_config_is_recognised()
    {
        // what Caddy answers on GET /config/ after it started from caddy/Caddyfile
        const string bootstrap = """{"admin":{"listen":"0.0.0.0:2019"},"apps":{"http":{"servers":{"srv0":{"listen":[":80"],"routes":[{"handle":[{"body":"Matcad: Caddy is running, waiting for configuration.","handler":"static_response","status_code":200}]}]}}}}}""";
        Assert.True(await Service(Answer(HttpStatusCode.OK, bootstrap)).RunsBootstrapConfigAsync());

        // Matcad's own config, and a Caddy that does not answer
        var ours = Service(Answer(HttpStatusCode.OK, "{}"));
        Assert.False(await Service(Answer(HttpStatusCode.OK, ours.BuildJson())).RunsBootstrapConfigAsync());
        Assert.False(await Service(Answer(HttpStatusCode.BadGateway, "")).RunsBootstrapConfigAsync());
        Assert.False(await Service(new Throwing()).RunsBootstrapConfigAsync());
        Assert.DoesNotContain(CaddyService.BootstrapAnswer, ours.BuildJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_bootstrap_answer_is_the_one_of_the_Caddyfile_of_the_image()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "caddy", "Caddyfile")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        Assert.Contains(CaddyService.BootstrapAnswer, File.ReadAllText(Path.Combine(dir!.FullName, "caddy", "Caddyfile")), StringComparison.Ordinal);
    }

    private CaddyService Service(HttpMessageHandler handler)
        => new(new Factory(handler), new CaddyConfigGenerator(_store, new RouteProvider(_store, new DockerRouteCache(), _cfg), _cfg), _store, _cfg, NullLogger<CaddyService>.Instance);

    private static Fixed Answer(HttpStatusCode status, string body) => new(status, body);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Fixed(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private sealed class Throwing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("Caddy is down");
    }
}
