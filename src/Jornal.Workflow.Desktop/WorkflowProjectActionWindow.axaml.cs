using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowProjectActionWindow : Window
{
    public WorkflowProjectActionWindow() => InitializeComponent();
    public WorkflowProjectActionWindow(string title, string message, string details, string confirm, string cancel = "Отмена") : this()
    { Title = ActionTitle.Text = title; ActionMessage.Text = message; ActionDetails.Text = details; ConfirmProjectAction.Label = confirm; CancelProjectAction.Label = cancel; }
    protected override void OnOpened(EventArgs e) { base.OnOpened(e); CancelProjectAction.Focus(); }
    protected override void OnKeyDown(KeyEventArgs e)
    { base.OnKeyDown(e); if (e.Key == Key.Escape) { Close(false); e.Handled = true; } }
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);
}
