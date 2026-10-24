namespace Jornal.Desktop.Tables;

public static class RowHighlightSettings
{
    public const double Minimum = 0;
    public const double Maximum = 100;
    public const double Default = 50;
    public const double Step = 5;

    // Ноль — мягкий, но видимый вариант, а не отключение индикации выбора.
    public static double Normalize(double value) => double.IsFinite(value)
        ? Math.Round(Math.Clamp(value, Minimum, Maximum) / Step, MidpointRounding.AwayFromZero) * Step : Default;
}
