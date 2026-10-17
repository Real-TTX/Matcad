using System.Globalization;

namespace Matcad.Controls;

/// <summary>
/// The accent colour a login page is branded with. The toolkit derives every other colour from the accent, but it cannot know whether text on
/// a free colour should be white or dark: this picks the one with the better contrast, as <c>matui.js</c> does for a colour chosen in the browser.
/// </summary>
public static class BrandAccent
{
    /// <summary>The colour as <c>#rrggbb</c>, or null when the text is not a <c>#rgb</c> or <c>#rrggbb</c> colour.</summary>
    public static string? Normalize(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length is not (4 or 7) || text[0] != '#' || !text.Skip(1).All(Uri.IsHexDigit))
            return null;

        return text.Length == 4
            ? "#" + string.Concat(text.Skip(1).Select(c => new string(c, 2))).ToLowerInvariant()
            : text.ToLowerInvariant();
    }

    /// <summary>White or the toolkit's dark ink, whichever reads better on <paramref name="hex"/> (a value from <see cref="Normalize"/>).</summary>
    public static string OnAccent(string hex)
    {
        var luminance = Luminance(hex);
        var onWhite = 1.05 / (luminance + 0.05);
        var onInk = (luminance + 0.05) / (Luminance("#0b1020") + 0.05);
        return onWhite >= 4.5 || onWhite >= onInk ? "#ffffff" : "#0b1020";
    }

    private static double Luminance(string hex)
    {
        double Channel(int start)
        {
            var c = int.Parse(hex.AsSpan(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
    }
}
