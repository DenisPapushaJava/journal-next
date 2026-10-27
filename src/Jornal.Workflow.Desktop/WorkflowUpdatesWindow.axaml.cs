using Avalonia.Controls;
using Avalonia.Input;
using Jornal.Desktop.ViewModels;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowUpdatesWindow : Window
{
    public WorkflowUpdatesWindow() => InitializeComponent();
    public bool CanClose => OwnedWindows.Count == 0 && DataContext is not AppUpdatesViewModel { IsApplying: true, RestartScheduled: false };
    protected override void OnOpened(EventArgs e)
    { base.OnOpened(e); UpdatesPanel.FocusCheck(); }
    protected override void OnClosing(WindowClosingEventArgs e)
    { if (!CanClose) e.Cancel = true; base.OnClosing(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape && CanClose) { Close(); e.Handled = true; }
    }
}
