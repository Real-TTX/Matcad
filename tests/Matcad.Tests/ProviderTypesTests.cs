using Matcad.Config;

namespace Matcad.Tests;

/// <summary>The DNS-provider registry: the two netcup faces (new CloudDNS and legacy) and how a
/// stored type maps to a Caddy DNS module.</summary>
public sealed class ProviderTypesTests
{
    [Fact]
    public void The_new_netcup_type_is_api_key_only_and_shown_as_Netcup()
    {
        var netcupCloud = ProviderTypes.Find("netcup_cloud");
        Assert.NotNull(netcupCloud);
        Assert.Equal("Netcup", netcupCloud!.DisplayName);
        Assert.Equal(["api_key"], netcupCloud.Fields.Select(f => f.Key));
    }

    [Fact]
    public void The_legacy_netcup_type_keeps_its_id_and_is_relabelled()
    {
        var legacy = ProviderTypes.Find("netcup");
        Assert.NotNull(legacy);
        Assert.Equal("Netcup legacy", legacy!.DisplayName);
        Assert.Equal(["customer_number", "api_key", "api_password"], legacy.Fields.Select(f => f.Key));
    }

    [Theory]
    [InlineData("netcup_cloud", "netcup")] // the new CloudDNS face compiles to the one netcup module
    [InlineData("netcup", "netcup")]       // the legacy face does too
    [InlineData("cloudflare", "cloudflare")]
    [InlineData("duckdns", "duckdns")]     // an unknown id is a custom module, used verbatim
    public void Module_maps_a_stored_type_to_its_caddy_module(string type, string module) =>
        Assert.Equal(module, ProviderTypes.Module(type));
}
