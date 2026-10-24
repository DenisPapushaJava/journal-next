using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowTrashConfirmationWindow : Window
{
    public WorkflowTrashConfirmationWindow() => InitializeComponent();
    protected override void OnOpened(EventArgs e) { base.OnOpened(e); CancelWorkflowTrash.Focus(); }
    protected override void OnClosing(WindowClosingEventArgs e)
    { if (DataContext is WorkflowTrashConfirmationViewModel { IsBusy: true }) e.Cancel = true; base.OnClosing(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    { base.OnKeyDown(e); if (e.Key == Key.Escape) { Close(); e.Handled = true; } }
    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
    private async void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkflowTrashConfirmationViewModel { CanEdit: true } model) return;
        await model.ConfirmCommand.ExecuteAsync(null); if (model.WasMoved) Close();
    }
}
