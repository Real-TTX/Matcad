namespace Matcad.Controls;

/// <summary>An item of the breadcrumb above a page; the last one is the page itself and has no address.</summary>
public sealed record Crumb(string Text, string? Url = null);

public static class Crumbs
{
    /// <summary>The trail below the app name: <c>Crumbs.Of(("Routes", "/Routes"), ("Edit route", null))</c>.</summary>
    public static IReadOnlyList<Crumb> Of(params (string Text, string? Url)[] items)
        => items.Select(i => new Crumb(i.Text, i.Url)).ToList();
}
