using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Jornal.Desktop.Views;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowShopProgressWindow : Window
{
    private bool _finished;
    public WorkflowShopProgressWindow() => InitializeComponent();
    internal void Finish(bool forceClose = false)
    {
        _finished = true;
        if (forceClose || DataContext is not WorkflowShopTransferViewModel model ||
            !model.HasError && !model.WasCancelled && !model.IsProgressSuccessful) { Close(); return; }
        ShopProgressClose.Content = "ОК";
        ShopProgressCancel.IsVisible = false; ShopProgressClose.IsVisible = true; ShopProgressClose.Focus();
    }
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_finished) { e.Cancel = true; (DataContext as WorkflowShopTransferViewModel)?.Cancel(); }
        base.OnClosing(e);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape) { Close(); e.Handled = true; }
    }
    private void OnCancel(object? sender, RoutedEventArgs e) => (DataContext as WorkflowShopTransferViewModel)?.Cancel();
    private void OnMapDetails(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkflowShopTransferViewModel { HasMapDetails: true } model) return;
        model.MapDetailsExpanded = !model.MapDetailsExpanded;
        Height = model.MapDetailsExpanded ? 580 : 380;
        WindowSizing.FitToWorkingArea(this);
    }
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
