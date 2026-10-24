using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowCatalogWindow : Window
{
    private readonly Func<WorkflowDatabaseViewModel>? _source;
    private bool _discard;
    public WorkflowCatalogWindow() => InitializeComponent();
    public WorkflowCatalogWindow(Func<WorkflowDatabaseViewModel> source) : this() => _source = source;
    private WorkflowCatalogViewModel? Model => DataContext as WorkflowCatalogViewModel;
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (Model is { IsBusy: true }) e.Cancel = true;
        else if (!_discard && Model is { HasChanges: true } model)
        { e.Cancel = true; model.Error = "Сохраните изменения или нажмите «Отмена»."; }
        base.OnClosing(e);
    }
    private void OnClose(object? sender, RoutedEventArgs e) { _discard = true; Close(); }
    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (_source is null || Model is not { CanEdit: true } model || OwnedWindows.Count > 0) return;
        if (model.HasChanges) { model.Error = "Сначала сохраните изменения станков и цен."; return; }
        var window = new WorkflowDatabaseWindow { DataContext = _source(), RequestedThemeVariant = ActualThemeVariant };
        await window.ShowDialog(this); model.Reload();
    }
}
