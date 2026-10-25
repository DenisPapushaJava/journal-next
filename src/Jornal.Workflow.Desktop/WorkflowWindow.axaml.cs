using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Jornal.Desktop.Clipboard;
using Jornal.Core.Results;
using Jornal.Workflow.Infrastructure;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowWindow : Window
{
    private readonly WorkflowPreferencesStore _preferences;
    private readonly WorkflowRestartStore _restart;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _loadingTheme = true;
    private WorkflowViewModel? Model => DataContext as WorkflowViewModel;
    public WorkflowWindow() : this(new WorkflowPreferencesStore(WorkflowIdentity.PreferencesPath)) { }
    public WorkflowWindow(WorkflowPreferencesStore preferences)
    {
        _preferences = preferences; _restart = new(Path.Combine(preferences.DirectoryPath, "update-restart.json")); InitializeComponent();
        try { ThemeSwitch.IsChecked = preferences.Load().DarkTheme; ApplyTheme(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        { RequestedThemeVariant = ThemeVariant.Light; }
        _loadingTheme = false;
    }
    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e); if (Model is not { } model) return;
        model.UseLocalPreferencesStore(_preferences);
        model.Updates.ConfigureRestart(() => model.CanUse && !model.HasPendingEdits && OwnedWindows.All(w => w is WorkflowSettingsWindow { CanRestart: true }),
            () => _restart.Save(model.CreateRestartDraft()), Close);
        await model.ReloadCommand.ExecuteAsync(null);
        if (model.Error is null)
        {
            try
            {
                if (_restart.Take(model.DatabaseId, remove: false) is { } draft && await model.RestoreRestartDraftAsync(draft, _lifetime.Token))
                    _restart.Take(model.DatabaseId, expected: draft);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { model.Error = "Не удалось восстановить результат после обновления. Сохранённые заявки доступны."; }
        }
        await model.Updates.RunAutomaticChecksAsync(_lifetime.Token);
    }
    protected override void OnClosing(WindowClosingEventArgs e)
    { if (Model?.IsBusy == true || OwnedWindows.OfType<WorkflowProjectEditorWindow>().Any() || Model?.Updates is { IsApplying: true, RestartScheduled: false }) e.Cancel = true; base.OnClosing(e); }
    protected override void OnClosed(EventArgs e)
    {
        _lifetime.Cancel(); Model?.Updates.ClearRestart(); Model?.Updates.CheckCommand.Cancel(); Model?.Updates.DownloadCommand.Cancel();
        Model?.Detach(); _lifetime.Dispose(); base.OnClosed(e);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || Model is not { CanUse: true } model) return;
        if (e.Key == Key.F5) { model.ReloadCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control && OwnedWindows.Count == 0)
        { WorkflowSearchInput.Focus(); WorkflowSearchInput.SelectAll(); e.Handled = true; }
    }
    private void OnClearSearch(object? sender, RoutedEventArgs e)
    {
        Model?.ClearSearchCommand.Execute(null);
        WorkflowSearchInput.Focus();
    }
    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Model is { } searching) { _ = searching.SearchDatabaseNowAsync(); e.Handled = true; return; }
        if (e.Key != Key.Escape || Model is not { HasSearchText: true } model) return;
        model.ClearSearchCommand.Execute(null); e.Handled = true;
    }
    private void OnThemeChanged(object? sender, RoutedEventArgs e)
    {
        if (_loadingTheme) return;
        ApplyTheme();
    }
    private void ApplyTheme() => RequestedThemeVariant = ThemeSwitch.IsChecked == true ? ThemeVariant.Dark : ThemeVariant.Light;
    private void OnOpenOrder(object? sender, TappedEventArgs e) => Model?.OpenOrderCommand.Execute(null);
    private void OnFocusNewOrder(object? sender, RoutedEventArgs e) => Dispatcher.UIThread.Post(() =>
    { if (IsVisible && Model is { IsCreating: true, CanUse: true }) NewManagerInput.Focus(); });
    private void OnFocusProject(object? sender, RoutedEventArgs e) => Dispatcher.UIThread.Post(() =>
    { if (IsVisible && Model is { IsPlanning: true, CanUse: true }) WorkflowProjectThickness.Focus(); });
    private async void OnCreateOrder(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanUse: true } model) return;
        await model.CreateOrderCommand.ExecuteAsync(null);
        if (IsVisible && model.IsOrderOpen && model.Error is null)
        { WorkflowOrderTabs.SelectedIndex = 0; Dispatcher.UIThread.Post(() => { if (IsVisible) PrepareWorkflowProject.Focus(); }); }
    }
    private async void OnSettings(object? sender, RoutedEventArgs e) => await ShowSettingsAsync(0);
    private async Task ShowSettingsAsync(int section)
    {
        if (Model is not { } model) return;
        if (OwnedWindows.OfType<WorkflowSettingsWindow>().FirstOrDefault() is { } existing)
        { if (existing.OwnedWindows.Count == 0) { existing.SelectSection(section); existing.Activate(); } return; }
        if (OwnedWindows.Count > 0 || !model.CanUse && section != 2) return;
        var window = new WorkflowSettingsWindow { DataContext = model.CreateGeneralSettings(), RequestedThemeVariant = ActualThemeVariant };
        window.SelectSection(section);
        await window.ShowDialog(this);
    }
    private async void OnMoveToTrash(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanUse: true } model || OwnedWindows.Count > 0 || model.CreateTrashConfirmation() is not { } confirmation) return;
        var window = new WorkflowTrashConfirmationWindow { DataContext = confirmation, RequestedThemeVariant = ActualThemeVariant };
        await window.ShowDialog(this);
    }
    private async void OnChangeResponsible(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Model is not { CanUse: true } model || OwnedWindows.Count > 0 || model.CreateResponsibleChange() is not { } request) return;
        var window = new WorkflowResponsibleWindow(request) { RequestedThemeVariant = ActualThemeVariant };
        var employeeId = await window.ShowDialog<Guid?>(this);
        if (employeeId is { } selected && IsVisible && !_lifetime.IsCancellationRequested)
            await model.ChangeResponsibleAsync(request, selected);
        if (IsVisible) WorkflowOrderActions.Focus();
    }
    private async void OnTrash(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanUse: true } model || OwnedWindows.Count > 0) return;
        var window = new WorkflowTrashWindow { DataContext = model.CreateTrash(), RequestedThemeVariant = ActualThemeVariant };
        await window.ShowDialog(this);
    }
    private async void OnCatalog(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanUse: true } model || OwnedWindows.Count > 0) return;
        var window = new WorkflowCatalogWindow(() => model.CreateDatabaseSettings()) { DataContext = model.CreateCatalogSettings(), RequestedThemeVariant = ActualThemeVariant };
        await window.ShowDialog(this);
    }
    private async void OnUpdates(object? sender, RoutedEventArgs e) => await ShowSettingsAsync(2);
    private async void OnCopyResult(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanUse: true } model || string.IsNullOrWhiteSpace(model.ResultText)) return;
        try { await new ResultClipboard(() => Clipboard).WriteAsync(ResultClipboardContent.FromText(model.ResultText)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { model.Error = "Не удалось скопировать результат."; }
    }
    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Model is not { CanUse: true } model) return;
        var path = (sender as Control)?.DataContext switch
        {
            WorkflowProjectRow row => row.FolderPath,
            SpecificationDraft draft => draft.FolderPath,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (!await OpenFolderAsync(path) && IsVisible && !_lifetime.IsCancellationRequested)
                model.Error = "Рабочая папка недоступна.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (IsVisible && !_lifetime.IsCancellationRequested) model.Error = "Рабочая папка недоступна."; }
    }
    protected virtual async Task<bool> OpenFolderAsync(string path)
    {
        var folder = await StorageProvider.TryGetFolderFromPathAsync(path);
        return folder is not null && IsVisible && !_lifetime.IsCancellationRequested &&
            await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
    }
    private async void OnRefreshReports(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanUse: true } model || OwnedWindows.Count > 0) return;
        var review = await model.CreateReportReviewAsync(_lifetime.Token);
        if (review is null || !IsVisible || _lifetime.IsCancellationRequested) return;
        var window = new WorkflowReportReviewWindow { DataContext = review, RequestedThemeVariant = ActualThemeVariant };
        await window.ShowDialog(this);
    }
    private async void OnOpenAcceptedReport(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanUse: true, SelectedDraft.Saved.Report: { } report } model) return;
        try
        {
            await Task.Run(() => WorkflowReportFiles.VerifyArchiveAsync(report, _lifetime.Token), _lifetime.Token);
            if (IsVisible && !await LaunchAcceptedReportAsync(report.ArchivePath))
                model.Error = "Не удалось открыть принятую спецификацию.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (IsVisible) model.Error = ex is WorkflowValidationException ? ex.Message : "Принятая копия отчёта недоступна."; }
    }
    protected virtual Task<bool> LaunchAcceptedReportAsync(string path) => Launcher.LaunchFileInfoAsync(new FileInfo(path));
}
