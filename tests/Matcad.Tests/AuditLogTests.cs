using System.Net;
using System.Text;
using Matcad.Config;
using Matcad.Data;
using Matcad.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Matcad.Tests;

/// <summary>The audit log: who changed what, by name, without a single value.</summary>
public sealed class AuditLogTests(MatcadFactory factory) : IClassFixture<MatcadFactory>
{
    private List<AuditEntry> Entries()
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<MatcadDbContext>().AuditEntries.AsNoTracking().OrderBy(e => e.Id).ToList();
    }

    private async Task<Browser> SignedInAsync(string name, UserRole role)
    {
        var auth = factory.Services.CreateScope().ServiceProvider.GetRequiredService<AuthService>();
        if (await auth.FindByLogin(name) == null)
            await auth.CreateUser(name, null, name + "-password", role, null);
        var store = factory.Services.GetRequiredService<ConfigStore>();
        var settings = store.Settings;
        settings.SetupCompleted = true;
        store.SaveSettings(settings);
        var browser = new Browser(factory);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.LoginAsync(name, name + "-password")).StatusCode);
        return browser;
    }

    [Fact]
    public async Task A_change_on_a_page_names_the_user_and_one_through_the_api_says_API()
    {
        var browser = await SignedInAsync("audit-admin", UserRole.Admin);

        await browser.PostFormAsync("/Routes/Edit", "/Routes/Edit", new()
        {
            ["Host"] = "audited.example.com", ["RouteType"] = "single", ["Target"] = "proxy", ["Upstream"] = "a:80", ["Enabled"] = "true",
        });
        var api = factory.CreateClient();
        api.DefaultRequestHeaders.Add("X-Api-Key", MatcadFactory.ApiKey);
        await api.PostAsync("/api/v1/providers", new StringContent("""{ "name": "audit-dns", "type": "netcup", "credentials": { "api_key": "audit-secret-value" } }""", Encoding.UTF8, "application/json"));
        var store = factory.Services.GetRequiredService<ConfigStore>();
        var id = store.Routes.Single(r => r.Host == "audited.example.com").Id;
        await browser.PostFormAsync($"/Routes/Edit?id={id}", $"/Routes/Edit?handler=Delete&id={id}", new() { ["Id"] = id.ToString() });

        var entries = Entries();
        Assert.Contains(entries, e => e is { Kind: "Route", Action: "created", Subject: "audited.example.com", Actor: "audit-admin" });
        Assert.Contains(entries, e => e is { Kind: "Route", Action: "deleted", Subject: "audited.example.com", Actor: "audit-admin" });
        Assert.Contains(entries, e => e is { Kind: "DNS provider", Action: "created", Subject: "audit-dns", Actor: "API" });
        Assert.DoesNotContain(entries, e => e.Subject.Contains("audit-secret-value") || e.Action.Contains("audit-secret-value"));
    }

    [Fact]
    public async Task User_accounts_are_logged_and_the_page_is_for_administrators()
    {
        var admin = await SignedInAsync("audit-admin2", UserRole.Admin);
        var user = await SignedInAsync("audit-user", UserRole.User);

        var (page, body) = await admin.GetAsync("/Settings/Audit?q=audit-user");
        var (denied, _) = await user.GetAsync("/Settings/Audit");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("audit-user", body);
        Assert.Contains(Entries(), e => e is { Kind: "User", Action: "created", Subject: "audit-user" });
        Assert.NotEqual(HttpStatusCode.OK, denied.StatusCode);
    }
}
