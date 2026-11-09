using System.Net;
using MatPlatform;
using Microsoft.AspNetCore.Hosting;

namespace Matcad.Tests;

/// <summary>Matcad with MAT_FRAME_ANCESTORS, as Mattiverse sets it (MatDesign docs/PLATFORM.md "Shown inside another app").</summary>
public sealed class FramedPlatformFactory : PlatformFactory
{
    public const string Mattiverse = "http://localhost:8030";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting(MatFrameAncestors.Variable, Mattiverse);
    }
}

/// <summary>Mattiverse may show Matcad in its window when MAT_FRAME_ANCESTORS names it; without it the pages keep the SAMEORIGIN of their forms.</summary>
public sealed class FrameAncestorsTests(PlatformFactory plain, FramedPlatformFactory framed) : IClassFixture<PlatformFactory>, IClassFixture<FramedPlatformFactory>
{
    [Fact]
    public async Task Without_the_variable_the_sign_in_page_cannot_be_framed_by_another_site()
    {
        var response = await plain.Browser().GetAsync("/Login?local=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("SAMEORIGIN", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.False(response.Headers.Contains("Content-Security-Policy"));
    }

    [Theory]
    [InlineData("/Login?local=1")]
    [InlineData("/api/health")]
    public async Task With_it_Mattiverse_may_frame_every_answer(string path)
    {
        var response = await framed.Browser().GetAsync(path);

        Assert.False(response.Headers.Contains("X-Frame-Options"));
        Assert.Equal("frame-ancestors 'self' http://localhost:8030", response.Headers.GetValues("Content-Security-Policy").Single());
    }
}
