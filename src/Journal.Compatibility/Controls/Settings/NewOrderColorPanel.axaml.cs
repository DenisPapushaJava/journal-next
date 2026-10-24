using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Jornal.Desktop.ViewModels;

namespace Jornal.Desktop.Controls.Settings;

public partial class NewOrderColorPanel : UserControl
{
    public NewOrderColorPanel() => InitializeComponent();
    private void OnCodeLostFocus(object? sender, RoutedEventArgs e) => (DataContext as MainWindowViewModel)?.CommitTechnologistColorCode();
    private void OnChooseColor(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel { UseTechnologistColor: true } model && sender is Button { DataContext: TechnologistColorOption option })
            model.SelectTechnologistColor(option.Argb);
        TechnologistColorButton.Flyout?.Hide();
    }
    private void OnColorWheelOpened(object? sender, EventArgs e) => TechnologistColorWheel.Focus();
    private void OnColorWheelClosed(object? sender, EventArgs e) => (DataContext as MainWindowViewModel)?.CommitTechnologistColorCode();
    private void OnColorWheelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        MoreTechnologistColors.Flyout?.Hide(); MoreTechnologistColors.Focus(); e.Handled = true;
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        TechnologistColorButton.Flyout?.Hide(); MoreTechnologistColors.Flyout?.Hide();
        base.OnDetachedFromVisualTree(e);
    }
}
