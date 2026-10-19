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

