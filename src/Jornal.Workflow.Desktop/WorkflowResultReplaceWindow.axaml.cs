using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowResultReplaceWindow : Window
{
    public WorkflowResultReplaceWindow() => InitializeComponent();
    protected override void OnOpened(EventArgs e) { base.OnOpened(e); KeepWorkflowResult.Focus(); }
    protected override void OnKeyDown(KeyEventArgs e)
    { base.OnKeyDown(e); if (e.Key == Key.Escape) { Close(false); e.Handled = true; } }
    private void OnKeep(object? sender, RoutedEventArgs e) => Close(false);
    private void OnReplace(object? sender, RoutedEventArgs e) => Close(true);
}
