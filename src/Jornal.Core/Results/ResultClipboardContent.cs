using System.Globalization;
using System.Net;
using System.Text;

namespace Jornal.Core.Results;

public sealed record ResultClipboardContent(string Text, string Html, string Rtf)
{
    public static ResultClipboardContent FromText(string text, ResultTextStyle? style = null)
    {
        var normalized = (style ?? new()).Normalize();
        return new(text, CreateHtml(text, normalized), CreateRtf(text, normalized));
    }

    private static string FontStyle(ResultTextStyle style) =>
        "font-family:Arial,sans-serif;font-size:" + style.FontSizePoints.ToString("0.#", CultureInfo.InvariantCulture) +
        "pt;color:" + style.ColorHex + ";";

    private static string CreateHtml(string text, ResultTextStyle style)
    {
        var font = FontStyle(style);
        var html = new StringBuilder("<div style=\"").Append(font).Append("line-height:1.4;\">");
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var count = text.Length == 0 ? 0 : lines.Length;
        for (var index = 0; index < count; index++)
        {
            html.Append("<div style=\"margin:0;\"><span style=\"").Append(font).Append("\">");
            // Короткий промежуток между полями; обычные пробелы позволяют перенос строк.
            // Исходные табуляции и выравнивание остаются в text/RTF без изменений.
            var encoded = WebUtility.HtmlEncode(lines[index].Replace("\t", " \u00a0 "));
            if (encoded.Length > 0) html.Append(encoded);
            // Mailspring удаляет завершающие пустые блоки. Обычный NBSP удерживает
            // последнюю пустую строку и точку ввода; text/RTF не меняются.
            else html.Append(index == count - 1 ? "&nbsp;" : "<br>");
            html.Append("</span></div>");
        }
        return html.Append("</div>").ToString();
    }

    private static string CreateRtf(string text, ResultTextStyle style)
    {
        // Самостоятельный простой RTF: Unicode, табуляции, абзацы. Цвет не зависит от темы приложения.
        var red = byte.Parse(style.ColorHex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var green = byte.Parse(style.ColorHex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var blue = byte.Parse(style.ColorHex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var rtf = new StringBuilder(@"{\rtf1\ansi\ansicpg1251\deff0\uc1{\fonttbl{\f0\fswiss Arial;}}{\colortbl;\red")
            .Append(red.ToString(CultureInfo.InvariantCulture)).Append(@"\green").Append(green.ToString(CultureInfo.InvariantCulture))
            .Append(@"\blue").Append(blue.ToString(CultureInfo.InvariantCulture)).Append(@";}\pard\f0\fs")
            .Append((style.FontSizePoints * 2).ToString("0", CultureInfo.InvariantCulture)).Append(@"\cf1 ");
        foreach (var character in text.ReplaceLineEndings("\n"))
        {
            switch (character)
            {
                case '\\': case '{': case '}': rtf.Append('\\').Append(character); break;
                case '\t': rtf.Append(@"\tab "); break;
                case '\n': rtf.Append(@"\par ").Append('\n'); break;
                default:
                    if (character is >= ' ' and <= '~') rtf.Append(character);
                    else rtf.Append(@"\u").Append(unchecked((short)character).ToString(CultureInfo.InvariantCulture)).Append('?');
                    break;
            }
        }
        return rtf.Append('}').ToString();
    }
}
