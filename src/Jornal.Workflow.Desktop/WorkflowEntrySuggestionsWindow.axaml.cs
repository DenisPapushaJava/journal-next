using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowEntrySuggestionsWindow : Window
{
    private WorkflowEntrySuggestionsViewModel? Model => DataContext as WorkflowEntrySuggestionsViewModel;
    public WorkflowEntrySuggestionsWindow() => InitializeComponent();
    protected override void OnOpened(EventArgs e)
    { base.OnOpened(e); if (Model is { } model) model.Saved += OnSaved; WorkflowManagersList.Focus(); }
    protected override void OnClosed(EventArgs e)
    { if (Model is { } model) model.Saved -= OnSaved; base.OnClosed(e); }
    protected override void OnClosing(WindowClosingEventArgs e)
    { if (Model is { IsBusy: true }) e.Cancel = true; base.OnClosing(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    { base.OnKeyDown(e); if (e.Key == Key.Escape) { Close(); e.Handled = true; } }
    private void OnSaved(object? sender, EventArgs e) => Close();
    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
