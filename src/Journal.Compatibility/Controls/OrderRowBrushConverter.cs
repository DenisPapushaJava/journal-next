using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;
using Jornal.Desktop.Tables;

namespace Jornal.Desktop.Controls;

/// <summary>Одинаковый расчёт строки и предпросмотра без изменения сохранённого ARGB.</summary>
public sealed class OrderRowBrushConverter : IMultiValueConverter
{
    public bool Foreground { get; set; }
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 5) return AvaloniaProperty.UnsetValue;
        var theme = values[4] as ThemeVariant ?? ThemeVariant.Light;
        Color Resource(string key) => ((ISolidColorBrush)Application.Current!.FindResource(theme, key)!).Color;
        var appearance = RowAppearance.Resolve(values[0] is int argb ? argb : null, values[1] is true, values[2] is true,
            values[3] is true && values.Count > 6 && values[6] is true,
            values.Count > 5 && values[5] is double strength ? strength : RowHighlightSettings.Default,
            Resource("JournalSurface"), Resource("JournalText"), Resource("JournalRowSelection"), Resource("JournalRowHover"));
        return new SolidColorBrush(Foreground ? appearance.Foreground : appearance.Background);
    }
}
