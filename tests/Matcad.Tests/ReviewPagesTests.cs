using System.Net;
using System.Reflection;
using Matcad.Config;
using Matcad.Data;
using Matcad.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;

namespace Matcad.Tests;

/// <summary>The web layer asks the services; it never holds the database itself (the owner's rule: models, business layer, frontend).</summary>
public class WebLayerTests
{
    [Fact]
    public void No_page_takes_the_database_context()
    {
        var pages = typeof(Program).Assembly.GetTypes().Where(t => typeof(PageModel).IsAssignableFrom(t) && !t.IsAbstract);
        var offenders = pages.Where(t => t.GetConstructors().Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(MatcadDbContext)))
                                         || t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Any(f => f.FieldType == typeof(MatcadDbContext)))
            .Select(t => t.FullName).ToList();

        Assert.Empty(offenders);
    }
}

public sealed class CertificatesPageTests(MatcadFactory factory) : IClassFixture<MatcadFactory>
{
    private async Task<Browser> SignedInAsync()
    {
        var auth = factory.Services.CreateScope().ServiceProvider.GetRequiredService<AuthService>();
        if (await auth.FindByLogin("cert-user") == null)
            await auth.CreateUser("cert-user", null, "cert-user-password", UserRole.User, null);
        var store = factory.Services.GetRequiredService<ConfigStore>();
        var settings = store.Settings;
        settings.SetupCompleted = true;
        store.SaveSettings(settings);
        var browser = new Browser(factory);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.LoginAsync("cert-user", "cert-user-password")).StatusCode);
        return browser;
    }

    [Fact]
    public async Task The_list_shows_every_certificate_and_the_check_can_be_asked_for()
    {
        var store = factory.Services.GetRequiredService<ConfigStore>();
        var provider = new ProviderConfig { Name = "cert-dns", Type = "netcup", Credentials = new() { ["api_key"] = "k" } };
        store.UpsertProvider(provider, null);
        store.UpsertRoute(new RouteConfig { Host = "*.certs.example.com", Wildcard = true, ProviderId = provider.Id, Upstream = "a:80" }, null);
        store.UpsertRoute(new RouteConfig { Host = "app.certs.example.com", Upstream = "a:80" }, null);
        store.UpsertRoute(new RouteConfig { Host = "alone.example.org", Upstream = "b:80" }, null);
        var browser = await SignedInAsync();

        var (response, body) = await browser.GetAsync("/Certificates");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("*.certs.example.com", body);
        Assert.Contains("app.certs.example.com", body); // covered by the wildcard
        Assert.Contains("alone.example.org", body);
        Assert.Contains("DNS (cert-dns)", body);
        Assert.DoesNotContain(">k<", body);

        var check = await browser.PostFormAsync("/Certificates", "/Certificates?handler=Check", new());
        Assert.Equal(HttpStatusCode.Redirect, check.StatusCode);

        var (_, filtered) = await browser.GetAsync("/Certificates?q=alone");
        Assert.DoesNotContain("*.certs.example.com", filtered);
    }

    [Fact]
    public async Task The_old_settings_tab_leads_to_the_page()
    {
        var auth = factory.Services.CreateScope().ServiceProvider.GetRequiredService<AuthService>();
        if (await auth.FindByLogin("cert-admin") == null)
            await auth.CreateUser("cert-admin", null, "cert-admin-password", UserRole.Admin, null);
        var browser = new Browser(factory);
        await browser.LoginAsync("cert-admin", "cert-admin-password");

        var (response, _) = await browser.GetAsync("/Settings?tab=certificates");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Certificates", response.Headers.Location?.OriginalString);
    }
}
