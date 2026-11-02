using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowHandoffClearWindow : Window
{
    public WorkflowHandoffClearWindow() => InitializeComponent();
    protected override void OnOpened(EventArgs e) { base.OnOpened(e); CancelHandoffClear.Focus(); }
    protected override void OnKeyDown(KeyEventArgs e)
    { base.OnKeyDown(e); if (e.Key == Key.Escape) { Close(false); e.Handled = true; } }
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);
}
