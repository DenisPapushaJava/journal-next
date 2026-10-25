using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowTrashWindow : Window
{
    private WorkflowTrashViewModel? Model => DataContext as WorkflowTrashViewModel;
    public WorkflowTrashWindow() => InitializeComponent();
    protected override async void OnOpened(EventArgs e)
    { base.OnOpened(e); if (Model is { } model) await model.ReloadCommand.ExecuteAsync(null); }
    protected override void OnClosing(WindowClosingEventArgs e)
    { if (Model is { IsBusy: true } || OwnedWindows.Count > 0) e.Cancel = true; base.OnClosing(e); }
    protected override void OnClosed(EventArgs e)
    { Model?.ReloadCommand.Cancel(); Model?.RestoreCommand.Cancel(); base.OnClosed(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.F5 && Model is { CanEdit: true } model) { model.ReloadCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
    private async void OnPurgeSelected(object? sender, RoutedEventArgs e) => await ConfirmPurgeAsync(false);
    private async void OnClearTrash(object? sender, RoutedEventArgs e) => await ConfirmPurgeAsync(true);
    private async Task ConfirmPurgeAsync(bool all)
    {
        if (OwnedWindows.Count > 0 || Model?.CreatePurgeConfirmation(all) is not { } confirmation) return;
        var window = new WorkflowTrashPurgeConfirmationWindow { DataContext = confirmation, RequestedThemeVariant = ActualThemeVariant };
        await window.ShowDialog(this);
        if (IsVisible) WorkflowTrashOrders.Focus();
    }
}
