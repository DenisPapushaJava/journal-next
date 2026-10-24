using Avalonia.Controls;
using Avalonia.Interactivity;
using Jornal.Desktop.ViewModels;

namespace Jornal.Desktop.Views;

public partial class DiagnosticsWindow : Window
{
    public DiagnosticsWindow() => InitializeComponent();
    protected override void OnClosing(WindowClosingEventArgs e)
    { if (DataContext is DiagnosticsViewModel { IsExporting: true }) e.Cancel = true; base.OnClosing(e); }
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
