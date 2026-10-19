using System.Net;
using System.Text.RegularExpressions;
using Matcad.Config;
using Matcad.Controls;
using Matcad.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Matcad.Tests;

/// <summary>The pages are built from the MatUi toolkit and nothing else: names that exist, no inline code, the app's colour as the registry has it.</summary>
public partial class MatUiToolkitTests
{
    private static readonly string AppFolder = FindAppFolder();

    private static string FindAppFolder()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder != null && !File.Exists(Path.Combine(folder.FullName, "src", "Matcad", "Matcad.csproj")))
            folder = folder.Parent;
        return Path.Combine(folder?.FullName ?? throw new InvalidOperationException("The repository was not found."), "src", "Matcad");
    }

    private static IEnumerable<string> Views() => Directory.EnumerateFiles(Path.Combine(AppFolder, "Pages"), "*.cshtml", SearchOption.AllDirectories);

    private static IReadOnlySet<string> SpriteIcons() => Regex.Matches(File.ReadAllText(Path.Combine(AppFolder, "wwwroot", "matui", "matui-icons.svg")), "<symbol id=\"mat-i-([a-z0-9-]+)\"")
        .Select(m => m.Groups[1].Value).ToHashSet();

    [GeneratedRegex(@"Ui\.Icon\(([^)]*)\)")]
    private static partial Regex IconCall();

    [GeneratedRegex("""<icon\s+name="([a-z0-9-]+)"|#mat-i-([a-z0-9-]+)""")]
    private static partial Regex IconName();

    [GeneratedRegex("\"([a-z0-9]+(?:-[a-z0-9]+)*)\"")]
    private static partial Regex Quoted();

    [Fact]
    public void Every_icon_a_page_names_is_in_the_sprite()
    {
        var sprite = SpriteIcons();
        var missing = new List<string>();
        foreach (var file in Views().Concat(Directory.EnumerateFiles(Path.Combine(AppFolder, "Controls"), "*.cs")))
        {
            var text = File.ReadAllText(file);
            var names = IconCall().Matches(text).SelectMany(m => Quoted().Matches(m.Groups[1].Value).Select(q => q.Groups[1].Value))
                .Concat(IconName().Matches(text).Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value));
            missing.AddRange(names.Where(n => !sprite.Contains(n)).Select(n => $"{Path.GetFileName(file)}: {n}"));
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void The_accent_is_the_one_the_toolkit_registry_has()
    {
        var css = File.ReadAllText(Path.Combine(AppFolder, "wwwroot", "matui", "matui.css"));

        var accent = Regex.Match(css, $"\\[data-app=\"{Ui.App.Id}\"\\]\\s*\\{{\\s*--mat-app-accent:\\s*(#[0-9a-fA-F]{{6}})").Groups[1].Value;

        Assert.Equal(Ui.App.Accent, accent);
    }

    [Fact]
    public void No_page_carries_an_inline_script_or_an_event_handler()
    {
        var offenders = Views()
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"<script(?![^>]*\bsrc=)[^>]*>|\son(click|change|submit|input|load)\s*="))
            .Select(Path.GetFileName);

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_page_carries_an_inline_style_except_the_colour_of_a_login_page()
    {
        var offenders = Views()
            .Where(f => Path.GetFileName(f) != "_PortalLayout.cshtml" && Regex.IsMatch(File.ReadAllText(f), @"\sstyle="))
            .Select(Path.GetFileName);

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_stylesheet_of_the_app_takes_its_colours_from_the_toolkit()
    {
        var css = File.ReadAllText(Path.Combine(AppFolder, "wwwroot", "css", "matcad.css"));

        Assert.DoesNotMatch(@"#[0-9a-fA-F]{3,8}\b|rgba?\(|gradient", Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline));
    }

    [Theory]
    [InlineData("#abc", "#aabbcc")]
    [InlineData("#2F6FEB", "#2f6feb")]
    [InlineData(" #0ea5e9 ", "#0ea5e9")]
    public void A_brand_colour_is_written_as_six_digits(string value, string expected) => Assert.Equal(expected, BrandAccent.Normalize(value));

    [Theory]
    [InlineData("")]
    [InlineData("red")]
    [InlineData("#12")]
    [InlineData("#12345")]
    [InlineData("#gggggg")]
    [InlineData("url(javascript:alert(1))")]
    [InlineData(null)]
    public void Anything_else_is_not_a_brand_colour(string? value) => Assert.Null(BrandAccent.Normalize(value));

    [Theory]
    [InlineData("#2f6feb", "#ffffff")]
    [InlineData("#111111", "#ffffff")]
    [InlineData("#ffeb3b", "#0b1020")]
    [InlineData("#ffffff", "#0b1020")]
    public void The_text_on_a_brand_colour_is_the_one_that_reads_better(string accent, string expected) => Assert.Equal(expected, BrandAccent.OnAccent(accent));

    [Fact]
    public void The_toolbar_links_keep_every_tool_but_the_one_that_is_removed()
    {
        var toolbar = new ToolbarModel
        {
            Id = "t", Action = "/List", Search = "api",
            Filters = [new FilterField("status", "Status", [new("enabled", "Active")], "enabled"), new FilterField("type", "Type", [new("a", "A")], null)],
            SortOptions = [new("name", "Name"), new("type", "Type")], DefaultSort = "name", Sort = "type", Descending = true,
        };

        Assert.Equal("/List?q=api&status=enabled&sort=type&dir=desc", toolbar.UrlWithout(""));
        Assert.Equal("/List?status=enabled&sort=type&dir=desc", toolbar.UrlWithout("q"));
        Assert.Equal("/List?q=api&sort=type&dir=desc", toolbar.UrlWithout("status"));
        Assert.Equal("/List?q=api&status=enabled", toolbar.UrlWithout("sort"));
        Assert.Equal(1, toolbar.ActiveFilters);
        Assert.True(toolbar.SortActive);
    }

    [Fact]
    public void Initials_are_the_first_letters_of_the_first_two_words()
    {
        Assert.Equal("AM", Ui.Initials("alex morgan"));
        Assert.Equal("M", Ui.Initials("matthias"));
        Assert.Equal("AB", Ui.Initials("a.b@example.org"));
    }

    [Fact]
    public void An_icon_name_that_is_not_a_name_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Ui.Icon("x\" onload=\"alert(1)"));
    }
}

/// <summary>The pages as the browser gets them.</summary>
public class MatUiPagesTests : IClassFixture<MatcadFactory>
{
    private readonly MatcadFactory _factory;
    public MatUiPagesTests(MatcadFactory factory) => _factory = factory;

    private async Task<Browser> SignedInAsync()
    {
        var auth = _factory.Services.CreateScope().ServiceProvider.GetRequiredService<AuthService>();
        if (await auth.FindByLogin("ui-tester") == null)
            await auth.CreateUser("ui-tester", null, "ui-tester-password", Matcad.Data.UserRole.Admin, null);
        var store = _factory.Services.GetRequiredService<ConfigStore>();
        var settings = store.Settings;
        settings.SetupCompleted = true;
        store.SaveSettings(settings);

        var browser = new Browser(_factory);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.LoginAsync("ui-tester", "ui-tester-password")).StatusCode);
        return browser;
    }

    private void SeedSomething()
    {
        var store = _factory.Services.GetRequiredService<ConfigStore>();
        if (store.Routes.Any(r => r.Host == "ui.example.com")) return;
        store.UpsertProvider(new ProviderConfig { Name = "UI provider", Type = "cloudflare", Credentials = new() { ["api_token"] = "x" } }, null);
        store.UpsertAuthentication(new AuthenticationConfig { Name = "UI basic", Type = AuthType.BasicAuth }, null);
        store.UpsertRoute(new RouteConfig { Host = "ui.example.com", Name = "ui", Upstream = "http://ui:8080", Enabled = true }, null);
        store.UpsertRoute(new RouteConfig { Host = "", ListenPort = 8123, Name = "port", Upstream = "http://ui:8081", Enabled = true }, null);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Routes")]
    [InlineData("/Routes/Edit")]
    [InlineData("/Authentications")]
    [InlineData("/Authentications/Edit")]
    [InlineData("/Providers")]
    [InlineData("/Providers/Edit")]
    [InlineData("/Certificates")]
    [InlineData("/Statistics")]
    [InlineData("/Settings")]
    [InlineData("/Settings?tab=users")]
    [InlineData("/Settings/User")]
    [InlineData("/Settings/Import")]
    [InlineData("/Account/Profile")]
    public async Task Every_page_of_the_app_has_the_frame_of_the_toolkit(string url)
    {
        SeedSomething();
        var browser = await SignedInAsync();

        var (response, body) = await browser.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<html lang=\"en\" data-app=\"matcad\"", body);
        Assert.Contains("class=\"mat-skip\"", body);
        Assert.Contains("<aside class=\"mat-nav\"", body);
        Assert.Contains("aria-label=\"Breadcrumb\"", body);
        Assert.Contains("class=\"mat-page__head\"", body);
        Assert.Contains("data-theme-toggle", body);
        Assert.Single(Regex.Matches(body, "<symbol id=\"mat-i-route\"")); // the sprite, inline and once
        Assert.Matches("<title>(?:[^<]+ (?:·|&#xB7;) )?Matcad</title>", body);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Routes")]
    [InlineData("/Authentications")]
    [InlineData("/Providers")]
    [InlineData("/Statistics")]
    [InlineData("/Settings")]
    public async Task Every_cell_of_a_table_has_a_label_for_the_phone(string url)
    {
        SeedSomething();
        var browser = await SignedInAsync();

        var (_, body) = await browser.GetAsync(url);

        var cells = Regex.Matches(body, "<td\\b[^>]*>").Select(m => m.Value);
        Assert.All(cells, cell => Assert.True(
            cell.Contains("data-label=") || cell.Contains("mat-table__cell-actions") || cell.Contains("colspan="), cell));
    }

    [Fact]
    public async Task The_routes_list_has_its_toolbar_above_and_the_actions_below()
    {
        SeedSomething();
        var browser = await SignedInAsync();

        var (_, body) = await browser.GetAsync("/Routes");

        var toolbar = body.IndexOf("class=\"mat-toolbar\"", StringComparison.Ordinal);
        var table = body.IndexOf("class=\"mat-table-wrap\"", StringComparison.Ordinal);
        var actions = body.IndexOf("class=\"mat-actions\"", StringComparison.Ordinal);
        Assert.True(toolbar > 0 && toolbar < table && table < actions);
        Assert.Contains("ui.example.com", body);
    }

    [Fact]
    public async Task The_routes_list_is_grouped_by_domain_with_folding_headings_and_the_add_row_last()
    {
        SeedSomething();
        var browser = await SignedInAsync();

        var (_, body) = await browser.GetAsync("/Routes");

        Assert.Matches("<table class=\"mat-table\" id=\"routes-table\" data-groups=\"routes\">", body);
        Assert.Contains("data-groups-set=\"toggle\" data-groups-target=\"#routes-table\"", body);
        var group = Regex.Match(body, "(?s)<tbody class=\"mat-group\"[^>]*data-group=\"example.com\"[^>]*>(.*?)</tbody>").Groups[1].Value;
        Assert.NotEmpty(group);
        Assert.Contains("class=\"mat-group__toggle\" data-group-toggle aria-expanded=\"true\"", group);
        var rows = Regex.Matches(group, "<tr class=\"(mat-group__[a-z]+)").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal("mat-group__head", rows.First());
        Assert.Equal("mat-group__add", rows.Last());
        Assert.Contains("mat-group__item", rows);
        // the action that adds a subdomain is the last row of the group, not part of its heading
        Assert.Contains("Add a subdomain", group[group.IndexOf("mat-group__add", StringComparison.Ordinal)..]);
        Assert.DoesNotContain("Add a subdomain", group[..group.IndexOf("mat-group__item", StringComparison.Ordinal)]);
        // under its domain a host shows what is its own and, muted, the rest
        Assert.Matches(@"<a href=""/Routes/Edit\?id=\d+"">ui</a><span class=""mat-muted"">\.example\.com</span>", group);
    }

    [Fact]
    public async Task A_search_keeps_the_groups_it_found_open_whatever_the_user_closed_before()
    {
        SeedSomething();
        var browser = await SignedInAsync();

        var (_, plain) = await browser.GetAsync("/Routes");
        var (_, searched) = await browser.GetAsync("/Routes?q=ui");

        Assert.DoesNotContain("data-group-fixed", plain);
        Assert.Contains("data-group-fixed>", searched);
    }

    [Fact]
    public async Task A_search_that_finds_nothing_says_so_and_offers_the_way_back()
    {
        SeedSomething();
        var browser = await SignedInAsync();

        var (_, body) = await browser.GetAsync("/Routes?q=nothing-called-this");

        Assert.Contains("Nothing matches", body);
        Assert.Contains("Reset the tools", body);
        Assert.Contains("value=\"nothing-called-this\"", body);
    }

    [Fact]
    public async Task A_save_in_the_settings_comes_back_to_the_same_tab()
    {
        var browser = await SignedInAsync();

        var response = await browser.PostFormAsync("/Settings?tab=docker", "/Settings?handler=Docker",
            new() { ["DockerEndpoint"] = "unix:///var/run/docker.sock", ["DockerBaseDomain"] = "" });

        Assert.Equal("/Settings?tab=docker", response.Headers.Location?.OriginalString);
        var (_, page) = await browser.GetAsync("/Settings?tab=docker");
        Assert.Matches("id=\"tab-docker\"[^>]*aria-selected=\"true\"", page);
        Assert.Matches("id=\"tab-general\"[^>]*aria-selected=\"false\"", page);
    }

    [Fact]
    public async Task The_sign_in_page_and_the_missing_page_are_a_card_without_a_menu()
    {
        var (login, loginBody) = await new Browser(_factory).GetAsync("/Login");
        var (missing, missingBody) = await (await SignedInAsync()).GetAsync("/nothing-here");

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("class=\"mat-auth\"", loginBody);
        Assert.Contains("mat-btn mat-btn--primary mat-btn--block", loginBody);
        Assert.DoesNotContain("class=\"mat-nav\"", loginBody);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("class=\"mat-auth\"", missingBody);
        Assert.Contains("Back to the dashboard", missingBody);
    }

    [Fact]
    public async Task The_login_portal_brings_its_own_toolkit_and_the_colour_of_its_authentication()
    {
        var store = _factory.Services.GetRequiredService<ConfigStore>();
        store.UpsertAuthentication(new AuthenticationConfig { Name = "Branded", Type = AuthType.Matcad, BrandTitle = "Portal X", BrandColor = "#E11D48", BrandLogoLayout = "top" }, null);
        var id = store.Authentications.Single(a => a.Name == "Branded").Id;
        var browser = new Browser(_factory);

        var (response, body) = await browser.GetAsync($"/auth/portal?auth={id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("data-accent=\"custom\"", body);
        Assert.Contains("--mat-custom-accent:#e11d48", body);
        Assert.Contains("--mat-custom-on-accent:#ffffff", body);
        Assert.Contains("Portal X", body);
        Assert.Contains("mc-brand mc-brand--top", body);
        // Caddy sends only /auth/* to Matcad on a protected host: nothing else may be needed.
        var references = Regex.Matches(body, "(?:href|src)=\"(/[^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(references);
        Assert.All(references, r => Assert.StartsWith("/auth/", r));
        foreach (var reference in references)
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(reference)).Response.StatusCode);
    }

    [Fact]
    public async Task A_portal_without_a_colour_keeps_the_colour_of_the_app()
    {
        var store = _factory.Services.GetRequiredService<ConfigStore>();
        store.UpsertAuthentication(new AuthenticationConfig { Name = "Plain", Type = AuthType.Matcad }, null);
        var id = store.Authentications.Single(a => a.Name == "Plain").Id;

        var (_, body) = await new Browser(_factory).GetAsync($"/auth/portal?auth={id}");

        Assert.DoesNotContain("data-accent", body);
        Assert.Matches(@"<html lang=""en"" data-app=""matcad""\s*>", body);
    }

    [Fact]
    public async Task The_offline_page_needs_only_files_the_service_worker_keeps()
    {
        var browser = new Browser(_factory);
        var (_, offline) = await browser.GetAsync("/offline.html");
        var (_, worker) = await browser.GetAsync("/sw.js");

        var needs = Regex.Matches(offline, "(?:href|src)=\"(/[^\"#]+)").Select(m => m.Groups[1].Value).Distinct().ToList();

        Assert.All(needs, file => Assert.Contains($"'{file}'", worker));
    }
}
