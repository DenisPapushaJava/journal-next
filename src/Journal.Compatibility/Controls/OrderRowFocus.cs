using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Jornal.Desktop.Controls;

/// <summary>Визуальный фокус клавиатуры отдельно от фокуса, оставшегося после клика.</summary>
public sealed class OrderRowFocus : AvaloniaObject
{
    public static readonly AttachedProperty<bool> KeyboardNavigationProperty =
        AvaloniaProperty.RegisterAttached<OrderRowFocus, DataGrid, bool>("KeyboardNavigation");

    static OrderRowFocus()
    {
        InputElement.GotFocusEvent.AddClassHandler<DataGrid>((grid, e) =>
        {
            if (e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional)
                grid.SetValue(KeyboardNavigationProperty, true);
            else if (e.NavigationMethod == NavigationMethod.Pointer)
                grid.SetValue(KeyboardNavigationProperty, false);
        }, handledEventsToo: true);
        // Клик по уже сфокусированному чекбоксу не вызывает GotFocus повторно.
        InputElement.PointerPressedEvent.AddClassHandler<DataGrid>((grid, _) => grid.SetValue(KeyboardNavigationProperty, false),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.KeyDownEvent.AddClassHandler<DataGrid>((grid, e) =>
        {
            if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Tab or Key.Space)
                grid.SetValue(KeyboardNavigationProperty, true);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public static bool GetKeyboardNavigation(DataGrid grid) => grid.GetValue(KeyboardNavigationProperty);
}
