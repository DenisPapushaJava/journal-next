namespace Jornal.Desktop.Customization;

public sealed record TableDensity(double RowHeight, double FontSize, double BadgeHeight)
{
    public const double MinimumHeight = 24;
    public const double MaximumHeight = 48;
    public const double DefaultHeight = 32;

    public static double NormalizeHeight(double height) => double.IsFinite(height) && height is >= MinimumHeight and <= MaximumHeight
        ? Math.Round(height, MidpointRounding.AwayFromZero) : DefaultHeight;

    public static TableDensity FromHeight(double height)
    {
        height = NormalizeHeight(height);
        var font = height <= 26 ? 12 : height < 36 ? 13 : 14;
        return new(height, font, Math.Min(height - 4, font + 11));
    }
}
