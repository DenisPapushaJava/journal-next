using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Jornal.Desktop.Controls;
using Jornal.Desktop.ViewModels;

namespace Jornal.Desktop.Views;

public partial class RowColorWindow : Window
{
    private RowColorViewModel? Model => DataContext as RowColorViewModel;
    public RowColorWindow() => InitializeComponent();
    protected override void OnOpened(EventArgs e) { base.OnOpened(e); WindowSizing.FitToWorkingArea(this); if (Model is { } model) model.Confirmed += OnConfirmed; }
    protected override void OnClosing(WindowClosingEventArgs e) { if (Model?.IsBusy == true) e.Cancel = true; base.OnClosing(e); }
    protected override void OnClosed(EventArgs e) { if (Model is { } model) model.Confirmed -= OnConfirmed; base.OnClosed(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && MoreRowColors.Flyout?.IsOpen == true)
        { MoreRowColors.Flyout.Hide(); MoreRowColors.Focus(); e.Handled = true; return; }
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape && Model?.IsBusy != true) { Close(); e.Handled = true; }
    }
    private void OnColorWheelOpened(object? sender, EventArgs e)
    {
        if (MoreRowColors.Flyout is Flyout { Content: Control content })
            content.GetVisualDescendants().OfType<ColorWheel>().FirstOrDefault()?.Focus();
    }
    private void OnSelectColor(object? sender, RoutedEventArgs e) { if (sender is Control { DataContext: TechnologistColorOption color }) Model?.Select(color.Argb); }
    private void OnConfirmed(object? sender, EventArgs e) => Close();
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
