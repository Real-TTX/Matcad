using Matcad.Config;

namespace Matcad.Tests;

public class RouteTreeAndCertificateTests
{
    private static RouteConfig Route(string host, bool wildcard = false, bool enabled = true, long? providerId = null) =>
        new() { Host = host, Wildcard = wildcard, Enabled = enabled, ProviderId = providerId };

    [Theory]
    [InlineData("app.example.com", "example.com", "app")]
    [InlineData("example.com", "example.com", "@")]
    [InlineData("*.example.com", "example.com", "*")]
    [InlineData("a.b.example.com", "example.com", "a.b")]
    public void The_tree_groups_by_base_domain(string host, string baseDomain, string label)
    {
        Assert.Equal(baseDomain, RouteTree.BaseDomain(host));
        Assert.Equal(label, RouteTree.SubLabel(host));
    }

    [Fact]
    public void Within_a_group_the_apex_comes_first_and_the_wildcard_last()
    {
        var groups = RouteTree.Build([Route("*.example.com", true), Route("b.example.com"), Route("example.com"), Route("a.example.com")]);

        var group = Assert.Single(groups);
        Assert.Equal(["example.com", "a.example.com", "b.example.com", "*.example.com"], group.Routes.Select(r => r.Host));
    }

    [Fact]
    public void A_wildcard_covers_subdomains_and_the_apex()
    {
        var routes = new[] { Route("*.example.com", true, providerId: 1), Route("app.example.com"), Route("example.com"), Route("other.org"), Route("localhost") };

        Assert.Equal(CertificatePlanner.CertKind.Wildcard, CertificatePlanner.ForRoute(routes[0], routes).Kind);
        Assert.Equal(CertificatePlanner.CertKind.Covered, CertificatePlanner.ForRoute(routes[1], routes).Kind);
        Assert.Equal(CertificatePlanner.CertKind.Covered, CertificatePlanner.ForRoute(routes[2], routes).Kind);
        Assert.Equal(CertificatePlanner.CertKind.Individual, CertificatePlanner.ForRoute(routes[3], routes).Kind);
        Assert.Equal(CertificatePlanner.CertKind.Internal, CertificatePlanner.ForRoute(routes[4], routes).Kind);
    }

    [Fact]
    public void A_disabled_wildcard_covers_nothing()
    {
        var routes = new[] { Route("*.example.com", true, enabled: false), Route("app.example.com") };

        Assert.Equal(CertificatePlanner.CertKind.Individual, CertificatePlanner.ForRoute(routes[1], routes).Kind);
    }

    [Fact]
    public void The_plan_lists_what_each_wildcard_certificate_covers()
    {
        var providers = new[] { new ProviderConfig { Id = 1, Name = "netcup" } };
        var routes = new[] { Route("*.example.com", true, providerId: 1), Route("example.com"), Route("app.example.com"), Route("solo.org") };

        var plan = CertificatePlanner.Plan(routes, providers);

        var wildcard = Assert.Single(plan.Wildcards);
        Assert.Equal("*.example.com, example.com", wildcard.Subjects);
        Assert.Equal("netcup", wildcard.Provider);
        Assert.Equal(["app.example.com", "example.com"], wildcard.CoveredHosts);
        Assert.Equal(["solo.org"], plan.Individual);
    }
}
