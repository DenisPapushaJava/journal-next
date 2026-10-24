namespace Jornal.Core.Results;

public sealed record ResultTextStyle
{
    public const decimal DefaultSize = 10;
    public static IReadOnlyList<decimal> FontSizes { get; } = Array.AsReadOnly<decimal>([8, 10, 12, 14]);
    public const string DefaultColor = "#B64C49";
    public decimal FontSizePoints { get; init; } = DefaultSize;
    public string ColorHex { get; init; } = DefaultColor;

    public ResultTextStyle Normalize() => this with
    {
        FontSizePoints = FontSizes.Contains(FontSizePoints) ? FontSizePoints : DefaultSize,
        ColorHex = ColorHex is { Length: 7 } && ColorHex[0] == '#' && ColorHex.Skip(1).All(char.IsAsciiHexDigit)
            ? ColorHex.ToUpperInvariant() : DefaultColor
    };
}
