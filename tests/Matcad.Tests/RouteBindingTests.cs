using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Matcad.Config;
using Matcad.Platform;
using MatPlatform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matcad.Tests;

/// <summary>Matcad provides <c>web.route</c> (MatDesign docs/PLATFORM.md): the definition says so and stays valid.</summary>
public sealed class RouteBindingDefinitionTests
{
    [Theory]
    [InlineData("en", "Makes reachable")]
    [InlineData("de", "Macht erreichbar")]
    public void Matcad_provides_web_route_and_may_sign_in_through_MatIdentity(string language, string label)
    {
        var definition = MatPlatformBuilder.Build(MatcadPlatform.Describe, language);

        Assert.Empty(MatPlatformValidator.Validate(definition));
        var provided = Assert.Single(definition.Provides);
        Assert.Equal(MatCapabilities.WebRoute, provided.Capability);
        Assert.Equal(1, provided.Version);
        Assert.Equal(label, provided.Label);
        var consumed = Assert.Single(definition.Consumes);
        Assert.Equal(MatCapabilities.IdentityOidc, consumed.Capability);
        Assert.True(consumed.Optional);
    }
}

/// <summary>
/// The business layer of the bindings (<see cref="RouteBindings"/>) on a config folder of its own: one route per consumer, moved by a new host,
/// a route of the same tool taken over, a route made by hand or by another tool never, the options of an administrator kept.
/// </summary>
public sealed class RouteBindingRulesTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "matcad-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConfigStore _store;

    public RouteBindingRulesTests()
    {
        _store = new ConfigStore(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Matcad:ConfigDir"] = _folder }).Build(),
            NullLogger<ConfigStore>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { /* temp folder */ }
    }

    private static RouteBindInput Drive(string? host = "drive.example.com", string? upstream = "http://mattiverse-matdrive-web:4810", string managedBy = "mattiverse")
        => new("matdrive", "MatDrive", host, upstream, managedBy);

    private RouteConfig Bind(RouteBindInput input)
    {
        var (route, refusal) = RouteBindings.Bind(_store, input, "en", Now);
        Assert.Null(refusal);
        return route!;
    }

    private RouteBindRefusal Refuse(RouteBindInput input)
    {
        var (route, refusal) = RouteBindings.Bind(_store, input, "en", Now);
        Assert.Null(route);
        return refusal!;
    }

    [Fact]
    public void A_binding_is_a_route_from_the_host_to_the_internal_address_named_after_the_consumer()
    {
        var route = Bind(Drive());

        var stored = Assert.Single(_store.Routes);
        Assert.Equal(route.Id, stored.Id);
        Assert.Equal("drive.example.com", stored.Host);
        Assert.Equal("http://mattiverse-matdrive-web:4810", stored.Upstream);
        Assert.Equal("MatDrive", stored.Name);
        Assert.Equal("mattiverse", stored.ManagedBy);
        Assert.Equal("matdrive", stored.Binding!.Consumer);
        Assert.Equal(Now, stored.Binding.Since);
        Assert.True(stored.Enabled);
        Assert.False(stored.Wildcard);
        Assert.Null(stored.ListenPort);
    }

    [Theory]
    [InlineData("drive.example.com", "https://drive.example.com")]
    [InlineData("matdrive.localhost", "http://matdrive.localhost")]
    [InlineData("matdrive.LOCALHOST", "http://matdrive.LOCALHOST")]
    public void The_public_address_is_https_except_for_a_development_name(string host, string url)
        => Assert.Equal(url, RouteBindings.PublicUrlOf(host));

    [Fact]
    public void The_same_request_twice_is_one_route_and_the_host_is_written_small()
    {
        var first = Bind(Drive("Drive.Example.COM", "http://mattiverse-matdrive-web:4810/"));
        var second = Bind(Drive());

        Assert.Equal(first.Id, second.Id);
        var stored = Assert.Single(_store.Routes);
        Assert.Equal("drive.example.com", stored.Host);
        Assert.Equal("http://mattiverse-matdrive-web:4810", stored.Upstream);
    }

    [Fact]
    public void A_new_host_moves_the_route_instead_of_making_a_second_one()
    {
        var first = Bind(Drive());

        var moved = Bind(Drive("files.example.org", "http://mattiverse-matdrive-web:4811"));

        Assert.Equal(first.Id, moved.Id);
        var stored = Assert.Single(_store.Routes);
        Assert.Equal("files.example.org", stored.Host);
        Assert.Equal("http://mattiverse-matdrive-web:4811", stored.Upstream);
        Assert.Equal(Now, stored.Binding!.Since);
    }

    [Fact]
    public void A_route_the_same_tool_made_through_the_rest_api_is_adopted()
    {
        // What Mattiverse made for every app before the bindings: host <slug>.<domain>, managedBy mattiverse, no consumer.
        _store.UpsertRoute(new RouteConfig
        {
            Host = "drive.example.com", Upstream = "http://old-container:4810", Name = "Old name", ManagedBy = "mattiverse", Enabled = true,
            Hsts = true, AuthenticationId = 7, AllowEmbedding = true,
        }, null);
        var before = Assert.Single(_store.Routes);

        var route = Bind(Drive());

        var stored = Assert.Single(_store.Routes);
        Assert.Equal(before.Id, route.Id);
        Assert.Equal("matdrive", stored.Binding!.Consumer);
        Assert.Equal("http://mattiverse-matdrive-web:4810", stored.Upstream);
        Assert.Equal("MatDrive", stored.Name);
        // What an administrator added to it stays.
        Assert.True(stored.Hsts);
        Assert.Equal(7, stored.AuthenticationId);
        Assert.True(stored.AllowEmbedding);
    }

    [Fact]
    public void Moving_onto_a_route_of_the_same_tool_takes_it_over_and_drops_the_old_one()
    {
        Bind(Drive());
        _store.UpsertRoute(new RouteConfig { Host = "files.example.org", Upstream = "http://old:1", Name = "Old", ManagedBy = "mattiverse", Enabled = true }, null);
        var held = _store.Routes.Single(r => r.Host == "files.example.org");

        var route = Bind(Drive("files.example.org"));

        var stored = Assert.Single(_store.Routes);
        Assert.Equal(held.Id, route.Id);
        Assert.Equal("matdrive", stored.Binding!.Consumer);
        Assert.Equal(Now, stored.Binding.Since);
    }

    [Theory]
    [InlineData(null, "made by hand")]
    [InlineData("matos", "managed by matos")]
    public void A_route_made_by_hand_or_by_another_tool_is_not_taken_over(string? managedBy, string said)
    {
        _store.UpsertRoute(new RouteConfig { Host = "drive.example.com", Upstream = "http://mine:80", Name = "Mine", ManagedBy = managedBy, Enabled = true }, null);

        var refusal = Refuse(Drive());

        Assert.True(refusal.Conflict);
        Assert.Equal("host", refusal.Field);
        Assert.Contains(said, refusal.Message);
        Assert.Contains("\"Mine\"", refusal.Message);
        var stored = Assert.Single(_store.Routes);
        Assert.Null(stored.Binding);
        Assert.Equal("http://mine:80", stored.Upstream);
    }

    [Fact]
    public void A_binding_by_hand_never_takes_over_a_route()
    {
        _store.UpsertRoute(new RouteConfig { Host = "drive.example.com", Upstream = "http://mine:80", Name = "Mine", Enabled = true }, null);

        Assert.True(Refuse(Drive(managedBy: RouteBindings.ByHand)).Conflict);
    }

    [Fact]
    public void The_host_of_another_consumer_is_refused_and_a_moving_route_stays_where_it_was()
    {
        Bind(new RouteBindInput("matoffice", "MatOffice", "office.example.com", "http://office:4820", "mattiverse"));
        Bind(Drive());

        var refusal = Refuse(Drive("office.example.com"));

        Assert.True(refusal.Conflict);
        Assert.Contains("matoffice", refusal.Message);
        Assert.Equal("drive.example.com", RouteBindings.Find(_store, "matdrive")!.Host);
        Assert.Equal("office.example.com", RouteBindings.Find(_store, "matoffice")!.Host);
    }

    [Fact]
    public void Without_host_and_target_nothing_is_made()
    {
        // What "Connect an app by hand" of the Connections area sends: a name only.
        var refusal = Refuse(new RouteBindInput("matdrive", "MatDrive", null, null, RouteBindings.ByHand));

        Assert.False(refusal.Conflict);
        Assert.Equal("", refusal.Field);
        Assert.Contains("Routes page", refusal.Message);
        Assert.Empty(_store.Routes);
    }

    [Fact]
    public void A_bound_consumer_asked_again_without_host_and_target_keeps_its_route()
    {
        // "New key" of the Connections area: a name only. There is nothing to change, so the values are the ones the route has.
        var route = Bind(Drive());

        var again = Bind(new RouteBindInput("matdrive", "MatDrive", null, null, "mattiverse"));

        Assert.Equal(route.Id, again.Id);
        var stored = Assert.Single(_store.Routes);
        Assert.Equal("drive.example.com", stored.Host);
        Assert.Equal("http://mattiverse-matdrive-web:4810", stored.Upstream);
    }

    [Theory]
    [InlineData("", "host")]
    [InlineData("https://drive.example.com", "host")]
    [InlineData("drive.example.com/path", "host")]
    [InlineData("drive.example.com:8443", "host")]
    [InlineData("*.example.com", "host")]
    [InlineData("192.168.1.10", "host")]
    [InlineData("drive", "host")]
    [InlineData("drive example.com", "host")]
    public void An_invalid_host_is_refused(string host, string field)
    {
        var refusal = Refuse(Drive(host));

        Assert.Equal(field, refusal.Field);
        Assert.False(refusal.Conflict);
        Assert.Empty(_store.Routes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ftp://drive:21")]
    [InlineData("http://drive:4810/sub")]
    [InlineData("http://drive:4810/?x=1")]
    [InlineData("http://user:pw@drive:4810")]
    [InlineData("drive:4810")]
    public void An_invalid_target_is_refused(string? upstream)
    {
        var refusal = Refuse(Drive(upstream: upstream));

        Assert.Equal("internalUrl", refusal.Field);
        Assert.Empty(_store.Routes);
    }

    [Fact]
    public void A_new_binding_keeps_what_an_administrator_changed_on_the_route()
    {
        var route = Bind(Drive());
        var edited = _store.Routes.Single();
        edited.Enabled = false;
        edited.Compress = true;
        edited.BlockedIps = ["203.0.113.0/24"];
        _store.UpsertRoute(edited, null);

        Bind(Drive());

        var stored = Assert.Single(_store.Routes);
        Assert.Equal(route.Id, stored.Id);
        Assert.False(stored.Enabled);
        Assert.True(stored.Compress);
        Assert.Equal(["203.0.113.0/24"], stored.BlockedIps);
    }

    [Fact]
    public void The_list_names_the_routes_of_bindings_only()
    {
        _store.UpsertRoute(new RouteConfig { Host = "other.example.com", Upstream = "http://other:80", Name = "Other", Enabled = true }, null);
        Bind(Drive());
        Bind(new RouteBindInput("matoffice", "MatOffice", "office.example.com", "http://office:4820", RouteBindings.ByHand));

        var list = RouteBindings.List(_store);

        Assert.Equal(["matdrive", "matoffice"], list.Select(r => r.Binding!.Consumer));
        Assert.Equal(["mattiverse", RouteBindings.ByHand], list.Select(RouteBindings.ManagedByOf));
        // A binding by hand is nobody's: the routes list shows no "Managed by" for it.
        Assert.Null(list[1].ManagedBy);
    }

    [Fact]
    public void Unbind_removes_the_route_only_for_who_manages_it()
    {
        Bind(Drive());
        _store.UpsertRoute(new RouteConfig { Host = "other.example.com", Upstream = "http://other:80", Name = "Other", Enabled = true }, null);

        Assert.False(RouteBindings.Unbind(_store, "matdrive", "someone-else"));
        Assert.False(RouteBindings.Unbind(_store, "matdrive", RouteBindings.ByHand));
        Assert.False(RouteBindings.Unbind(_store, "nobody", null));
        Assert.NotNull(RouteBindings.Find(_store, "matdrive"));

        Assert.True(RouteBindings.Unbind(_store, "matdrive", "mattiverse"));
        Assert.Null(RouteBindings.Find(_store, "matdrive"));
        Assert.Equal(["other.example.com"], _store.Routes.Select(r => r.Host));
        Assert.False(RouteBindings.Unbind(_store, "matdrive", "mattiverse"));
    }

    [Fact]
    public void Unbind_without_a_manager_removes_any_and_by_hand_matches_a_route_without_one()
    {
        Bind(Drive(managedBy: RouteBindings.ByHand));
        Assert.True(RouteBindings.Unbind(_store, "matdrive", RouteBindings.ByHand));

        Bind(Drive());
        Assert.True(RouteBindings.Unbind(_store, "matdrive", null));
        Assert.Empty(_store.Routes);
    }
}

/// <summary>
/// The broker endpoints of MatPlatform with Matcad's handler, the whole app in memory: <c>PUT /api/bindings/web.route/{consumer}</c> with the key
/// and <c>params.host</c>, the list, the removal by <c>managedBy</c>, the refusals (400, 409), <c>/api/v1</c> unchanged and the Connections area.
/// </summary>
public sealed class RouteBindingApiTests(PlatformFactory site) : IClassFixture<PlatformFactory>
{
    private ConfigStore Store => site.Services.GetRequiredService<ConfigStore>();

    private static JsonContent Body(string consumer, string? host, string? internalUrl = null, string managedBy = "mattiverse")
        => JsonContent.Create(new
        {
            version = 1,
            managedBy,
            consumer = new { name = consumer, url = host is null ? null : "https://" + host, internalUrl },
            @params = host is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["host"] = host },
        });

    private static async Task<JsonObject> Json(HttpResponseMessage response, HttpStatusCode status)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {text}");
        return JsonNode.Parse(text)!.AsObject();
    }

    [Fact]
    public async Task A_broker_binds_an_app_with_its_host_and_gets_the_public_address()
    {
        var api = site.Api();

        var answer = await Json(await api.PutAsync("/api/bindings/web.route/matdrive",
            Body("MatDrive", "drive.example.com", "http://mattiverse-matdrive-web:4810")), HttpStatusCode.OK);

        Assert.Equal("https://drive.example.com", (string?)answer["variables"]![MatPlatformSettings.PublicUrlVariable]);
        var route = RouteBindings.Find(Store, "matdrive")!;
        Assert.Equal("drive.example.com", route.Host);
        Assert.Equal("http://mattiverse-matdrive-web:4810", route.Upstream);
        Assert.Equal("MatDrive", route.Name);
        Assert.Equal("mattiverse", route.ManagedBy);

        var list = await Json(await api.GetAsync("/api/bindings"), HttpStatusCode.OK);
        var binding = list["bindings"]!.AsArray().Single(b => (string?)b!["consumer"] == "matdrive")!;
        Assert.Equal(MatCapabilities.WebRoute, (string?)binding["capability"]);
        Assert.Equal("MatDrive", (string?)binding["name"]);
        Assert.Equal("mattiverse", (string?)binding["managedBy"]);
        Assert.NotNull(binding["since"]);

        // The same request again is the same route; a development name gets http.
        await Json(await api.PutAsync("/api/bindings/web.route/matdrive",
            Body("MatDrive", "drive.example.com", "http://mattiverse-matdrive-web:4810")), HttpStatusCode.OK);
        Assert.Single(Store.Routes, r => r.Binding?.Consumer == "matdrive");
        var dev = await Json(await api.PutAsync("/api/bindings/web.route/matdev",
            Body("MatDev", "matdev.localhost", "http://matdev:80")), HttpStatusCode.OK);
        Assert.Equal("http://matdev.localhost", (string?)dev["variables"]![MatPlatformSettings.PublicUrlVariable]);
    }

    [Fact]
    public async Task A_route_the_broker_made_through_the_engine_api_is_adopted()
    {
        var engine = site.Api(PlatformFactory.EngineKey);
        var made = await Json(await engine.PostAsync("/api/v1/routes", JsonContent.Create(new
        {
            host = "notes.example.com", wildcard = false, target = "proxy", upstream = "http://old-notes:80", enabled = true, name = "MatNote",
            managedBy = "mattiverse",
        })), HttpStatusCode.OK);
        var id = (long)made["route"]!["id"]!;

        await Json(await site.Api().PutAsync("/api/bindings/web.route/matnote",
            Body("MatNote", "notes.example.com", "http://mattiverse-matnote-web:2301")), HttpStatusCode.OK);

        var route = Assert.Single(Store.Routes, r => r.Host == "notes.example.com");
        Assert.Equal(id, route.Id);
        Assert.Equal("matnote", route.Binding!.Consumer);
        Assert.Equal("http://mattiverse-matnote-web:2301", route.Upstream);
    }

    [Fact]
    public async Task A_route_made_by_hand_on_the_host_is_a_conflict()
    {
        Store.UpsertRoute(new RouteConfig { Host = "wiki.example.com", Upstream = "http://wiki:80", Name = "Wiki", Enabled = true }, null);

        var answer = await Json(await site.Api().PutAsync("/api/bindings/web.route/matwiki",
            Body("MatWiki", "wiki.example.com", "http://mattiverse-matwiki-web:80")), HttpStatusCode.Conflict);

        Assert.Contains("Wiki", (string?)answer["errors"]!["host"]);
        Assert.Null(Store.Routes.Single(r => r.Host == "wiki.example.com").Binding);
    }

    [Fact]
    public async Task A_binding_without_host_or_target_is_a_bad_request()
    {
        var api = site.Api();

        var noHost = await Json(await api.PutAsync("/api/bindings/web.route/maths", Body("MatHs", null, "http://maths:80")), HttpStatusCode.BadRequest);
        var noTarget = await Json(await api.PutAsync("/api/bindings/web.route/maths", Body("MatHs", "maths.example.com")), HttpStatusCode.BadRequest);
        var neither = await Json(await api.PutAsync("/api/bindings/web.route/maths", Body("MatHs", null)), HttpStatusCode.BadRequest);
        var invalid = await Json(await api.PutAsync("/api/bindings/web.route/maths", Body("MatHs", "https://maths.example.com/", "http://maths:80")), HttpStatusCode.BadRequest);

        Assert.NotNull(noHost["errors"]!["host"]);
        Assert.NotNull(noTarget["errors"]!["internalUrl"]);
        Assert.NotNull(neither["errors"]![""]);
        Assert.NotNull(invalid["errors"]!["host"]);
        Assert.Null(RouteBindings.Find(Store, "maths"));
    }

    [Fact]
    public async Task A_binding_is_removed_only_by_who_manages_it()
    {
        var api = site.Api();
        await Json(await api.PutAsync("/api/bindings/web.route/matbill", Body("MatBill", "bill.example.com", "http://matbill:2505")), HttpStatusCode.OK);

        Assert.Equal(HttpStatusCode.NotFound, (await api.DeleteAsync("/api/bindings/web.route/matbill?managedBy=manual")).StatusCode);
        Assert.NotNull(RouteBindings.Find(Store, "matbill"));

        Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync("/api/bindings/web.route/matbill?managedBy=mattiverse")).StatusCode);
        Assert.Null(RouteBindings.Find(Store, "matbill"));
        Assert.DoesNotContain(Store.Routes, r => r.Host == "bill.example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await api.DeleteAsync("/api/bindings/web.route/matbill?managedBy=mattiverse")).StatusCode);
    }

    [Fact]
    public async Task Only_the_key_of_the_app_binds()
    {
        var engineOnly = await site.Api(PlatformFactory.EngineKey).PutAsync("/api/bindings/web.route/matkey", Body("MatKey", "key.example.com", "http://matkey:80"));
        var none = await site.Api(null).PutAsync("/api/bindings/web.route/matkey", Body("MatKey", "key.example.com", "http://matkey:80"));

        Assert.Equal(HttpStatusCode.Unauthorized, engineOnly.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Null(RouteBindings.Find(Store, "matkey"));
    }

    [Fact]
    public async Task The_engine_api_shows_a_bound_route_as_before_and_keeps_it_bound()
    {
        await Json(await site.Api().PutAsync("/api/bindings/web.route/matpaper", Body("MatPaper", "paper.example.com", "http://matpaper:8080")), HttpStatusCode.OK);
        var engine = site.Api(PlatformFactory.EngineKey);

        var manual = JsonNode.Parse(await engine.GetStringAsync("/api/v1/routes/manual"))!.AsArray();
        var dto = manual.Single(r => (string?)r!["host"] == "paper.example.com")!.AsObject();
        // The shape of /api/v1 is unchanged: no field of the binding comes out.
        Assert.Equal(
            ["id", "name", "host", "wildcard", "upstream", "insecureSkipVerify", "fallbackUrl", "redirectPermanent", "authenticationId", "providerId",
             "acmeEmail", "enabled", "allowEmbedding", "listenPort", "upstreamHost", "upstreamHeaders", "requireHeader", "allowedIps", "managedBy"],
            dto.Select(p => p.Key));
        Assert.Equal("http://matpaper:8080", (string?)dto["upstream"]);
        Assert.Equal("mattiverse", (string?)dto["managedBy"]);

        // An old caller that upserts the same host through /api/v1 (without the mark) leaves it the binding of the app.
        var answer = await Json(await engine.PostAsync("/api/v1/routes", JsonContent.Create(new
        {
            host = "paper.example.com", wildcard = false, target = "proxy", upstream = "http://matpaper:8081", enabled = true, name = "MatPaper",
        })), HttpStatusCode.OK);
        Assert.Equal((long)dto["id"]!, (long)answer["route"]!["id"]!);
        var route = RouteBindings.Find(Store, "matpaper")!;
        Assert.Equal("http://matpaper:8081", route.Upstream);
        Assert.Equal("mattiverse", route.ManagedBy);
    }

    [Fact]
    public async Task The_connections_area_lists_the_bindings_and_refuses_a_route_from_a_name_alone()
    {
        await Json(await site.Api().PutAsync("/api/bindings/web.route/matshape", Body("MatShape", "shape.example.com", "http://matshape:3939")), HttpStatusCode.OK);
        var browser = site.Browser();
        // A German browser: the area is English all the same, like every page of Matcad.
        browser.DefaultRequestHeaders.AcceptLanguage.ParseAdd("de-DE");
        Assert.Equal(HttpStatusCode.Redirect, (await PlatformFactory.SignInLocallyAsync(browser, "admin", PlatformFactory.AdminPassword)).StatusCode);

        var page = await browser.GetStringAsync("/Settings/Connections");
        Assert.Contains("data-connections", page);
        Assert.Contains("Connect an app by hand", page);
        Assert.Contains("<code>web.route</code>", page);
        Assert.Contains("<code>identity.oidc</code>", page);
        Assert.Contains("MatShape <code>matshape</code>", page);
        Assert.Contains("href=\"/Settings/Connections\"", page);

        // "Connect an app by hand" gives a name only: refused, with the message in the area, and no route.
        var before = Store.Routes.Count;
        var post = await browser.PostAsync(MatConnectionEndpoints.FormPath, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = PlatformFactory.Token(page),
            ["capability"] = MatCapabilities.WebRoute,
            ["returnUrl"] = "/Settings/Connections",
            ["do"] = "connect",
            ["name"] = "MatHand",
        }));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Equal("/Settings/Connections", post.Headers.Location!.OriginalString);
        Assert.Equal(before, Store.Routes.Count);
        Assert.Null(RouteBindings.Find(Store, "mathand"));

        var after = await browser.GetStringAsync("/Settings/Connections");
        var alert = Regex.Match(after, "<div class=\"mat-alert mat-alert--danger\" role=\"alert\">.*?</div></div>", RegexOptions.Singleline);
        Assert.True(alert.Success, after);
        Assert.Contains("(host)", alert.Value);
        Assert.Contains("Routes page", alert.Value);

        // "New key" of a bound app: a route has no key, nothing changes, and the area shows its public address.
        var renew = await browser.PostAsync(MatConnectionEndpoints.FormPath, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = PlatformFactory.Token(after),
            ["capability"] = MatCapabilities.WebRoute,
            ["returnUrl"] = "/Settings/Connections",
            ["do"] = "renew",
            ["consumer"] = "matshape",
        }));
        Assert.Equal(HttpStatusCode.Redirect, renew.StatusCode);
        after = await browser.GetStringAsync("/Settings/Connections");
        Assert.Contains("mat-alert--success", after);
        Assert.Contains("https://shape.example.com", after);
        Assert.Equal("http://matshape:3939", RouteBindings.Find(Store, "matshape")!.Upstream);

        // Remove from the area: the route goes.
        var token = PlatformFactory.Token(after);
        var remove = await browser.PostAsync(MatConnectionEndpoints.FormPath, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["capability"] = MatCapabilities.WebRoute,
            ["returnUrl"] = "/Settings/Connections",
            ["do"] = "remove",
            ["consumer"] = "matshape",
        }));
        Assert.Equal(HttpStatusCode.Redirect, remove.StatusCode);
        Assert.Null(RouteBindings.Find(Store, "matshape"));
        Assert.DoesNotContain(Store.Routes, r => r.Host == "shape.example.com");

        // The audit log names the administrator who removed it there, and the key for what the broker did.
        var audit = await site.Services.GetRequiredService<Matcad.Services.AuditLog>().ListAsync("shape.example.com", null, 10);
        Assert.Contains(audit.Entries, e => e.Action == "deleted" && e.Actor == "admin");
        Assert.Contains(audit.Entries, e => e.Action == "created" && e.Actor == "API");
    }

    [Fact]
    public async Task The_connections_view_offers_web_route_with_its_bindings()
    {
        await Json(await site.Api().PutAsync("/api/bindings/web.route/matboard", Body("MatBoard", "board.example.com", "http://matboard:3945")), HttpStatusCode.OK);

        using var view = JsonDocument.Parse(await site.Api().GetStringAsync("/api/connections"));
        var offer = Assert.Single(view.RootElement.GetProperty("offers").EnumerateArray());
        Assert.Equal(MatCapabilities.WebRoute, offer.GetProperty("capability").GetString());
        Assert.Contains(offer.GetProperty("bindings").EnumerateArray(), b => b.GetProperty("consumer").GetString() == "matboard");
        var use = Assert.Single(view.RootElement.GetProperty("uses").EnumerateArray());
        Assert.Equal(MatCapabilities.IdentityOidc, use.GetProperty("capability").GetString());
    }
}
