using System.Globalization;
using Avalonia.Data.Converters;

namespace Jornal.Desktop.Controls;

/// <summary>Видимая область данных без закреплённой колонки чекбоксов 24 px.</summary>
public sealed class OrderRowOutlineWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double width && double.IsFinite(width) ? Math.Max(0, width - 24) : 0d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
