using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Jornal.Workflow.Desktop;

internal enum WorkflowCloseChoice { Cancel, Discard, Save }

public partial class WorkflowCloseWindow : Window
{
    public WorkflowCloseWindow() => InitializeComponent();
    internal WorkflowCloseWindow(WorkflowCloseRequest request) : this()
    {
        Title = request.Title; CloseHeading.Text = request.Title; CloseMessage.Text = request.Message;
        DiscardWorkflowClose.Label = request.Application ? "Выйти без сохранения" : "Закрыть без сохранения";
        SaveWorkflowClose.IsVisible = request.CanSave;
    }
    protected override void OnOpened(EventArgs e) { base.OnOpened(e); CancelWorkflowClose.Focus(); }
    protected override void OnKeyDown(KeyEventArgs e)
    { base.OnKeyDown(e); if (e.Key == Key.Escape) { Close(WorkflowCloseChoice.Cancel); e.Handled = true; } }
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(WorkflowCloseChoice.Cancel);
    private void OnDiscard(object? sender, RoutedEventArgs e) => Close(WorkflowCloseChoice.Discard);
    private void OnSave(object? sender, RoutedEventArgs e) => Close(WorkflowCloseChoice.Save);
}
