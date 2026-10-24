using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Jornal.Desktop.Controls;

internal sealed record ResultButtonPalette(Color Background, Color Foreground, Color Hover, Color Pressed, Color Border)
{
    internal static ResultButtonPalette Create(string hex)
    {
        var background = Color.Parse(hex);
        var navy = Color.Parse("#244265");
        var foreground = Contrast(background, navy) >= 4.5 ? navy :
            Contrast(background, Colors.White) >= 4.5 ? Colors.White : Colors.Black;
        var target = Luminance(background) < .03 ? Colors.White : Colors.Black;
        Color State(double amount)
        {
            var color = Blend(background, target, amount);
            while (Contrast(color, foreground) < 4.5 && amount > .001)
                color = Blend(background, target, amount /= 2);
            return color;
        }
        var border = Contrast(background, Color.Parse("#F1F2F4")) >= 3 ? background : Color.Parse("#647F9D");
        return new(background, foreground, State(.12), State(.24), border);
    }

    internal void Apply(JournalButton button)
    {
        button.Resources["ResultActionBackground"] = new ImmutableSolidColorBrush(Background);
        button.Resources["ResultActionForeground"] = new ImmutableSolidColorBrush(Foreground);
        button.Resources["ResultActionHover"] = new ImmutableSolidColorBrush(Hover);
        button.Resources["ResultActionPressed"] = new ImmutableSolidColorBrush(Pressed);
        button.Resources["ResultActionBorder"] = new ImmutableSolidColorBrush(Border);
    }

    internal static double Contrast(Color a, Color b) => (Math.Max(Luminance(a), Luminance(b)) + .05) /
        (Math.Min(Luminance(a), Luminance(b)) + .05);
    private static double Luminance(Color color)
    {
        static double Channel(byte value) => value / 255d <= .04045 ? value / 255d / 12.92 : Math.Pow((value / 255d + .055) / 1.055, 2.4);
        return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
    }
    private static Color Blend(Color a, Color b, double amount) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * amount), (byte)Math.Round(a.G + (b.G - a.G) * amount), (byte)Math.Round(a.B + (b.B - a.B) * amount));
}
