using Avalonia.Media;
using Jornal.Core.Orders;

namespace Jornal.Desktop.Tables;

public readonly record struct RowAppearance(Color Background, Color Foreground)
{
    public static RowAppearance Resolve(int? argb, bool selected, bool hovered, bool keyboardFocused, double strength,
        Color surface, Color text, Color selection, Color hover)
    {
        var colored = argb is { } value && value != OrderColors.None;
        var stored = colored ? Color.FromUInt32(unchecked((uint)argb!.Value)) : surface;
        var normal = Mix(surface, stored, stored.A / 255d);
        var active = hovered || keyboardFocused;
        var background = normal;
        var textBackground = normal;
        var amount = RowHighlightSettings.Normalize(strength) / 100;
        double Scale(double low, double normal, double high) => amount <= .5
            ? low + (normal - low) * amount * 2 : normal + (high - normal) * (amount - .5) * 2;

        if (selected || active)
        {
            // Прежний принцип: исходный цвет без смягчения, выбор — RGB × 0,6.
            // Тёмные отметки осветляем; направление не меняется при движении ползунка.
            var endpoint = Ink(normal);
            var maximum = Contrast(normal, endpoint);
            Color Shade(double amount) => endpoint == Colors.Black
                ? Color.FromRgb((byte)(normal.R * (1 - amount)), (byte)(normal.G * (1 - amount)), (byte)(normal.B * (1 - amount)))
                : Mix(normal, endpoint, amount);
            double Target(Color candidate, double minimum) => Math.Max(minimum, Contrast(normal, candidate));

            var hoverSeed = colored ? Shade(Scale(.05, .10, .16)) : Mix(normal, hover, Scale(.7, 1, 1));
            var hoverTarget = Math.Min(Target(hoverSeed, Scale(1.12, 1.25, 1.65)), maximum / 1.25);
            var selectedSeed = colored ? Shade(Scale(.25, .40, .55))
                : amount <= .5 ? Mix(normal, selection, Scale(.8, 1, 1)) : Mix(selection, endpoint, Scale(0, 0, .12));
            var selectedTarget = Math.Min(Math.Max(Target(selectedSeed, Scale(1.50, 1.90, 3.40)), hoverTarget * 1.16), maximum / 1.08);
            var bothSeed = colored ? Shade(Scale(.31, .46, .61)) : Mix(selectedSeed, endpoint, Scale(.06, .08, .10));
            var bothTarget = Math.Min(Math.Max(Target(bothSeed, Scale(1.75, 2.25, 4)), selectedTarget * 1.08), maximum);
            background = FitContrast(normal, selected ? hovered ? bothSeed : selectedSeed : hoverSeed,
                endpoint, selected ? hovered ? bothTarget : selectedTarget : hoverTarget);
            // Наведение меняет только фон выбранной строки, не переключает чёрный/белый текст.
            textBackground = selected && hovered
                ? FitContrast(normal, selectedSeed, endpoint, selectedTarget) : background;
        }
        var foreground = !colored && Contrast(text, textBackground) >= 4.5 ? text : Ink(textBackground);
        if (selected && hovered) background = KeepTextReadable(textBackground, background, foreground);
        return new(background, foreground);
    }

    private static Color KeepTextReadable(Color baseline, Color candidate, Color foreground)
    {
        if (Contrast(foreground, candidate) >= 4.5) return candidate;
        double low = 0, high = 1;
        for (var step = 0; step < 18; step++)
        {
            var middle = (low + high) / 2;
            if (Contrast(foreground, Mix(baseline, candidate, middle)) >= 4.5) low = middle;
            else high = middle;
        }
        return Mix(baseline, candidate, low);
    }

    private static Color FitContrast(Color normal, Color seed, Color endpoint, double target)
    {
        if ((Luminance(seed) - Luminance(normal)) * (Luminance(endpoint) - Luminance(normal)) <= 0)
            seed = normal;
        if (Contrast(normal, seed) == target) return seed;
        if (Contrast(normal, seed) >= target) return ReachContrast(normal, normal, seed, target);
        return ReachContrast(normal, seed, endpoint, target);
    }

    private static Color ReachContrast(Color original, Color seed, Color endpoint, double target)
    {
        double low = 0, high = 1;
        // Подбираем контраст по фактической яркости, учитывая округление каналов RGB.
        for (var step = 0; step < 18; step++)
        {
            var middle = (low + high) / 2;
            if (Contrast(original, Mix(seed, endpoint, middle)) >= target) high = middle;
            else low = middle;
        }
        return Mix(seed, endpoint, high);
    }
    public static Color Ink(Color background) => Luminance(background) > 0.179 ? Colors.Black : Colors.White;
    public static double Contrast(Color first, Color second)
    {
        var a = Luminance(first); var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
    private static Color Mix(Color first, Color second, double amount) => Color.FromRgb(
        (byte)Math.Round(first.R + (second.R - first.R) * amount),
        (byte)Math.Round(first.G + (second.G - first.G) * amount),
        (byte)Math.Round(first.B + (second.B - first.B) * amount));
    private static double Luminance(Color color)
    {
        static double Linear(byte value) => value / 255d <= 0.04045 ? value / 255d / 12.92 : Math.Pow((value / 255d + 0.055) / 1.055, 2.4);
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }
}
