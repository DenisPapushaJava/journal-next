using Avalonia.Controls;
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
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
