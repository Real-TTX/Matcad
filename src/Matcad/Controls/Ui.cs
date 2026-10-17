using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;

namespace Matcad.Controls;

/// <summary>Small helpers for the markup of the MatUi toolkit: icons, the logo tile, the first letters of a name and the names of the app.</summary>
public static partial class Ui
{
    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex NameRegex();

    /// <summary>An icon from the page's inline sprite. The name must exist in <c>matui-icons.svg</c> (a unit test checks the pages).</summary>
    public static IHtmlContent Icon(string name, string? extraClass = null)
    {
        if (!NameRegex().IsMatch(name))
            throw new ArgumentException($"'{name}' is not an icon name.", nameof(name));

        return new HtmlString(Svg(name, extraClass));
    }

    private static string Svg(string name, string? extraClass)
    {
        var cls = string.IsNullOrEmpty(extraClass) ? "mat-icon" : $"mat-icon {extraClass}";
        return $"<svg class=\"{cls}\" aria-hidden=\"true\" focusable=\"false\"><use href=\"#mat-i-{name}\"></use></svg>";
    }

    /// <summary>The logo of Matcad in the tile of the toolkit (<c>mat-logo</c>). The picture has its own rounded corners, so the tile is transparent (<c>mc-logo</c>).
    /// <paramref name="imageBase"/> is where the pictures are served: the login portal is shown on a protected host, where only /auth/* reaches Matcad.</summary>
    public static IHtmlContent Logo(string size = "md", string imageBase = "/img")
    {
        var cls = size == "md" ? "mat-logo mc-logo" : $"mat-logo mat-logo--{size} mc-logo";
        return new HtmlString($"<span class=\"{cls}\" role=\"img\" aria-label=\"{App.Name}\"><img src=\"{imageBase}/logo.svg\" alt=\"\" width=\"128\" height=\"128\" /></span>");
    }

    /// <summary>The "Managed by Mattiverse" tag of an object that another tool writes through the REST API (config field
    /// <c>ManagedBy</c>), with the hint that manual changes may be overwritten. Empty when nobody manages the object;
    /// another value shows as "Managed by &lt;value&gt;".</summary>
    public static IHtmlContent ManagedBy(string? managedBy)
    {
        var by = managedBy?.Trim();
        if (string.IsNullOrEmpty(by)) return HtmlString.Empty;
        var name = by.Equals("mattiverse", StringComparison.OrdinalIgnoreCase) ? "Mattiverse" : by;
        return new HtmlString($"<span class=\"mat-tag\" title=\"{Encode($"Created by {name}. Manual changes may be overwritten.")}\">" +
            $"{Encode($"Managed by {name}")}</span>");
    }

    /// <summary>The first letters of a name, for an avatar.</summary>
    public static string Initials(string name)
        => string.Concat(name.Split([' ', '-', '_', '.', '@'], StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpperInvariant(p[0])));

    /// <summary>A UTC time that the browser shows in its own time zone (matcad.js); without script it reads as UTC.
    /// <paramref name="kind"/> is short, datetime or date.</summary>
    public static IHtmlContent LocalTime(DateTime utc, string kind = "datetime")
    {
        var instant = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return new HtmlString($"<time data-local=\"{kind}\" datetime=\"{instant:yyyy-MM-ddTHH:mm:ss}Z\">{instant:yyyy-MM-dd HH:mm} UTC</time>");
    }

    /// <summary>Text with the characters of HTML escaped, for the few places that build markup in C#.</summary>
    public static string Encode(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    /// <summary>The version as the menu shows it (the build string, <c>local</c> on a developer machine).</summary>
    public static string Version { get; } =
        typeof(Ui).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "local";

    /// <summary>How the toolkit's registry knows Matcad (the accent itself comes from <c>data-app</c> in <c>matui.css</c>).</summary>
    public static class App
    {
        public const string Id = "matcad";
        public const string Name = "Matcad";
        public const string Tagline = "Reverse proxy with automatic HTTPS";

        /// <summary>The accent as the toolkit registry has it; it colours the bar of the browser. A unit test compares it with <c>matui.css</c>.</summary>
        public const string Accent = "#2f6feb";
    }
}
