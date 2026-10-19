using System.Net;
using System.Text;
using Matcad.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matcad.Tests;

public class DnsCredentialTesterTests
{
    /// <summary>Answers every request with one body and remembers what was asked.</summary>
    private sealed class FakeHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static DnsCredentialTester Tester(FakeHandler handler) => new(new Factory(handler), NullLogger<DnsCredentialTester>.Instance);

    [Fact]
    public async Task An_active_cloudflare_token_passes_and_is_sent_as_a_bearer_token()
    {
        var handler = new FakeHandler("""{ "success": true, "result": { "id": "x", "status": "active" } }""");

        var (ok, message) = await Tester(handler).TestAsync("cloudflare", new Dictionary<string, string> { ["api_token"] = "cf-token" });

        Assert.True(ok);
        Assert.Contains("Zone.DNS", message);
        Assert.Equal(DnsCredentialTester.CloudflareVerifyEndpoint, handler.Last!.RequestUri!.ToString());
        Assert.Equal("Bearer cf-token", handler.Last.Headers.Authorization!.ToString());
    }

    [Theory]
    [InlineData("""{ "success": false, "errors": [ { "code": 1000, "message": "Invalid API Token" } ] }""", HttpStatusCode.Unauthorized)]
    [InlineData("""{ "success": true, "result": { "status": "disabled" } }""", HttpStatusCode.OK)]
    public async Task A_rejected_or_disabled_token_fails_without_repeating_it(string body, HttpStatusCode status)
    {
        var (ok, message) = await Tester(new FakeHandler(body, status)).TestAsync("cloudflare", new Dictionary<string, string> { ["api_token"] = "cf-token" });

        Assert.False(ok);
        Assert.DoesNotContain("cf-token", message);
    }

    [Fact]
    public async Task A_missing_token_is_said_before_any_call()
    {
        var handler = new FakeHandler("{}");

        var (ok, _) = await Tester(handler).TestAsync("cloudflare", new Dictionary<string, string> { ["api_token"] = " " });

        Assert.False(ok);
        Assert.Null(handler.Last);
    }
}
