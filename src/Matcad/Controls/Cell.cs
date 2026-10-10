using System.Net;
using Microsoft.AspNetCore.Html;

namespace Matcad.Controls;

/// <summary>
/// Helpers to build table cells safely. Text is HTML-encoded; the markup
/// helpers compose from already-encoded pieces. Used by the list pages to fill
/// <see cref="DataRow"/> cells.
/// </summary>
public static class Cell
{
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    /// <summary>A UTC time that the browser shows in its own time zone (matcad.js); without script it reads as UTC.
    /// <paramref name="kind"/> is short, datetime or date.</summary>
    public static IHtmlContent LocalTime(DateTime utc, string kind = "datetime")
    {
        var instant = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return new HtmlString($"<time data-local=\"{kind}\" datetime=\"{instant:yyyy-MM-ddTHH:mm:ss}Z\">{instant:yyyy-MM-dd HH:mm} UTC</time>");
    }

    public static IHtmlContent Text(string? value) => new HtmlString(E(value));

    public static IHtmlContent Muted(string? value) =>
        new HtmlString($"<span class=\"text-muted\">{E(value)}</span>");

    /// <summary>A badge; <paramref name="kind"/> is info (default), success, warning, danger or muted.</summary>
    public static IHtmlContent Badge(string text, string kind = "info") =>
        new HtmlString($"<span class=\"mat-badge{(kind == "info" ? "" : $" mat-badge--{kind}")}\">{E(text)}</span>");
}
