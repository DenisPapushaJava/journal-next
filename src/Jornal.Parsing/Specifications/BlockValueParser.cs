using System.Globalization;
using System.Text.RegularExpressions;

namespace Jornal.Parsing.Specifications;

internal static partial class BlockValueParser
{
    public static string Space(string value) => Whitespace().Replace(value, " ").Trim();

    public static bool Number(string text, out decimal value)
    {
        text = Space(text);
        value = 0;
        return Numeric().IsMatch(text) && decimal.TryParse(text.Replace(" ", "").Replace(',', '.'),
            NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    public static bool Integer(string text, out int value)
    {
        value = 0;
        return Number(text, out var parsed) && parsed == decimal.Truncate(parsed) && parsed <= int.MaxValue && (value = (int)parsed) >= 0;
    }

    public static bool Dimensions(string text, out SheetDimensions value)
    {
        value = null!;
        var match = DimensionsPattern().Match(Space(text));
        if (!match.Success || !Number(match.Groups[1].Value, out var thickness) || !Number(match.Groups[2].Value, out var length) ||
            !Number(match.Groups[3].Value, out var width) || thickness <= 0 || length <= 0 || width <= 0) return false;
        value = new(thickness, length, width); return true;
    }

    public static bool Duration(string text, out TimeSpan value)
    {
        value = default;
        var match = DurationPattern().Match(Space(text));
        if (!match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out var minutes) || minutes > 59 ||
            !int.TryParse(match.Groups[3].Success ? match.Groups[3].Value : "0", CultureInfo.InvariantCulture, out var seconds) || seconds > 59) return false;
        try { value = TimeSpan.FromTicks(checked((checked(hours * 3600) + minutes * 60 + seconds) * TimeSpan.TicksPerSecond)); return true; }
        catch (OverflowException) { return false; }
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)] private static partial Regex Whitespace();
    [GeneratedRegex(@"^(?:[0-9]+|[0-9]{1,3}(?: [0-9]{3})+)(?:[.,][0-9]+)?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Numeric();
    [GeneratedRegex(@"^([0-9]+(?:[.,][0-9]+)?)\s*[xXхХ×]\s*([0-9]+(?:[.,][0-9]+)?)\s*[xXхХ×]\s*([0-9]+(?:[.,][0-9]+)?)(?:\s*(?:мм|mm))?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex DimensionsPattern();
    [GeneratedRegex(@"^([0-9]+):([0-9]{2})(?::([0-9]{2}))?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex DurationPattern();
}
