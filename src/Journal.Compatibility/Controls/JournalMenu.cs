using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using SvgControl = Avalonia.Svg.Svg;

namespace Jornal.Desktop.Controls;

internal static class JournalMenu
{
    internal static void OnContextKeyUp(object? sender, KeyEventArgs e)
    {
        // Avalonia по умолчанию поддерживает Menu; добавляем привычный Shift+F10.
        if (e.Handled || e.Key != Key.F10 || e.KeyModifiers != KeyModifiers.Shift || e.Source is not Control target) return;
        target.RaiseEvent(new ContextRequestedEventArgs());
        e.Handled = true;
    }

    internal static MenuItem Item(string name, string text, bool enabled, EventHandler<RoutedEventArgs> action,
        Func<bool> valid, Action close, string? icon = null, string? hint = null, bool danger = false)
    {
        var item = new MenuItem { Name = name, Header = text, IsEnabled = enabled };
        if (danger) item.Classes.Add("danger");
        if (hint is not null) ToolTip.SetTip(item, hint);
        if (icon is not null)
        {
            var svg = new SvgControl(new Uri("avares://Jornal.Desktop/"))
            { Path = $"avares://Jornal.Desktop/Assets/Icons/{icon}.svg", Width = 16, Height = 16, IsHitTestVisible = false };
            item.Icon = svg;
            item.PropertyChanged += (_, e) =>
            {
                if (e.Property == MenuItem.ForegroundProperty && item.Foreground is ISolidColorBrush brush)
                    SvgControl.SetCurrentColor(svg, brush.Color);
            };
        }
        item.Click += (sender, e) =>
        {
            e.Handled = true;
            if (!item.IsEffectivelyEnabled || !valid()) return;
            close();
            action(sender, e);
        };
        return item;
    }

    internal static MenuItem Heading(string text) => new() { Header = text, IsEnabled = false, Classes = { "context-heading" } };
}
