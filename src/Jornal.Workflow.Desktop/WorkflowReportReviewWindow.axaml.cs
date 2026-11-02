using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;
using System.ComponentModel;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowReportReviewWindow : Window
{
    private readonly Func<WorkflowReportCandidate, WorkflowCatalogWindow>? _catalog;
    private WorkflowReportReviewViewModel? _observed;
    public WorkflowReportReviewWindow()
    {
        InitializeComponent(); DataContextChanged += OnModelChanged;
    }
    public WorkflowReportReviewWindow(Func<WorkflowReportCandidate, WorkflowCatalogWindow> catalog) : this() => _catalog = catalog;
    private void OnModelChanged(object? sender, EventArgs e)
    {
        if (_observed is not null) _observed.PropertyChanged -= OnReviewChanged;
        _observed = DataContext as WorkflowReportReviewViewModel;
        if (_observed is not null) _observed.PropertyChanged += OnReviewChanged;
    }
    private void OnReviewChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (IsVisible && _observed is { IsGuided: true, IsBusy: false, Error: null } guided && !guided.GuidedCandidates.Any()) { Close(); return; }
        if (e.PropertyName == nameof(WorkflowReportReviewViewModel.IsBusy) &&
            _observed is { WasSaved: true, IsBusy: false }) Close();
    }
    protected override void OnClosing(WindowClosingEventArgs e)
    { if (OwnedWindows.Count > 0 || DataContext is WorkflowReportReviewViewModel { IsBusy: true }) e.Cancel = true; base.OnClosing(e); }
    protected override void OnClosed(EventArgs e)
    {
        DataContextChanged -= OnModelChanged;
        if (_observed is not null) _observed.PropertyChanged -= OnReviewChanged;
        _observed = null; base.OnClosed(e);
    }
    private async void OnConfigurePrices(object? sender, RoutedEventArgs e)
    {
        if (_catalog is null || OwnedWindows.Count > 0 || DataContext is not WorkflowReportReviewViewModel { CanEdit: true, Selected.HasDraft: true } model) return;
        var window = _catalog(model.Selected!); window.RequestedThemeVariant = ActualThemeVariant;
        await window.ShowDialog(this);
        if (IsVisible) model.RefreshPricing(refreshSelected: false);
    }
    private async void OnOpenProjectFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkflowReportReviewViewModel { CanEdit: true, Selected: { } candidate } model || OwnedWindows.Count > 0) return;
        var path = Path.GetDirectoryName(candidate.Project.NativePath ?? candidate.Snapshot?.SourcePath);
        if (path is null) { model.Error = "Папка проекта не задана."; return; }
        try { if (!await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path))) model.Error = "Не удалось открыть папку проекта. Проверьте доступ к сети."; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (IsVisible) model.Error = "Папка проекта недоступна. Проверьте путь и доступ к сети."; }
    }
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
