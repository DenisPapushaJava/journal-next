using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Jornal.Workflow.Infrastructure;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowWindow : Window
{
    private readonly WorkflowPreferencesStore _preferences;
    private readonly WorkflowRestartStore _restart;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _loadingTheme = true;
    private bool _loadingStartupJournal;
    private WorkflowViewModel? Model => DataContext as WorkflowViewModel;
    public WorkflowWindow() : this(new WorkflowPreferencesStore(WorkflowIdentity.PreferencesPath)) { }
    public WorkflowWindow(WorkflowPreferencesStore preferences, WorkflowPreferences? initialPreferences = null, TimeProvider? journalRefreshClock = null,
        Func<string, Action<bool>, Action, Task<IDisposable>>? journalSubscribe = null, TimeProvider? mapStatusClock = null, TimeProvider? resultFeedbackClock = null,
        TimeProvider? shopStatusClock = null)
    {
        _mapStatusClock = mapStatusClock ?? TimeProvider.System;
        _resultFeedbackClock = resultFeedbackClock ?? TimeProvider.System;
        _shopStatusClock = shopStatusClock ?? TimeProvider.System;
        _journalClock = journalRefreshClock; _journalSubscribe = journalSubscribe;
        _preferences = preferences; _restart = new(Path.Combine(preferences.DirectoryPath, "update-restart.json"));
        WorkflowPreferences settings;
        try { settings = initialPreferences ?? preferences.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        { settings = new(); }
        RequestedThemeVariant = settings.DarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;
        InitializeComponent(); InitializeJournalInputTracking(); InitializeOrderContextMenu(); InitializeMapSigningStatus(); InitializeResultFeedback(); InitializeShopStatus(); ThemeSwitch.IsChecked = settings.DarkTheme; ApplyTheme();
        Activated += OnOfficeWorkspaceActivated;
        _loadingTheme = false;
    }
    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e); SyncMapSigningStatus(); SyncShopStatus(); ScheduleResultFeedbackExpiration(); if (Model is not { } model) return;
        model.UseLocalPreferencesStore(_preferences);
        model.Updates.ConfigureRestart(() => model.CanUse && !model.IsCopyingCuttingTask && !model.HasPendingEdits && !model.HasUnfinishedTaskText && OwnedWindows.All(w => w is WorkflowSettingsWindow { CanRestart: true } or WorkflowUpdatesWindow { CanClose: true }),
            () => _restart.Save(model.CreateRestartDraft()), Close);
        _loadingStartupJournal = true;
        try { await model.ReloadCommand.ExecuteAsync(null); }
        finally { _loadingStartupJournal = false; }
        if (!IsVisible || !ReferenceEquals(Model, model) || _lifetime.IsCancellationRequested) return;
        if (model.Error is null)
        {
            try
            {
                if (_restart.Take(model.DatabaseId, remove: false) is { } draft && await model.RestoreRestartDraftAsync(draft, _lifetime.Token))
                    _restart.Take(model.DatabaseId, expected: draft);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            { model.Error = "Не удалось восстановить результат после обновления. Сохранённые заявки доступны."; }
        }
        InitializeJournalRefresh(model);
        RefreshOfficeWorkspace();
        await model.Updates.RunAutomaticChecksAsync(_lifetime.Token);
    }
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_confirmedApplicationClose && Model is { } model && !model.Updates.RestartScheduled)
        {
            if (_officeCheckChoice is { } check && !_officeCheckForExit)
            {
                e.Cancel = true;
                _closeAfterOfficeCheck = true;
                check.TrySetResult(false);
            }
            else if (_closeDialogPending || OwnedWindows.Count > 0) e.Cancel = true;
            else if (!model.CanCloseWorkspace)
                e.Cancel = !(_loadingStartupJournal && model.CanCancelInitialOfficeRead);
            else if (model.PrepareClose(application: true) is { } request)
            {
                var checkOfficeFiles = model.HasOfficeWorkingCopies && !model.ShowOfflineJournal && e.CloseReason != WindowCloseReason.OSShutdown;
                if (request.NeedsConfirmation || checkOfficeFiles)
                { e.Cancel = true; _ = RequestCloseAsync(application: true, checkOfficeFiles); }
            }
        }
        base.OnClosing(e);
    }
    protected override void OnClosed(EventArgs e)
    {
        Activated -= OnOfficeWorkspaceActivated;
        Model?.ReloadCommand.Cancel();
        StopOrderContextMenu(); StopResultFeedback(); StopMapSigningStatus(); StopShopStatus(); StopJournalRefresh(); _pressedPointers.Clear(); _lifetime.Cancel(); Model?.Updates.ClearRestart(); Model?.Updates.CheckCommand.Cancel(); Model?.Updates.DownloadCommand.Cancel();
        Model?.Detach(); _lifetime.Dispose(); base.OnClosed(e);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!e.Handled && e.Key == Key.Escape && _officeCheckChoice?.TrySetResult(false) == true)
        { e.Handled = true; return; }
        OnCuttingTaskTextKeyDown(this, e); if (e.Handled) return;
        base.OnKeyDown(e);
        if (e.Handled || Model is not { CanUse: true } model) return;
        if (e.Key == Key.F5) { model.ReloadCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control && OwnedWindows.Count == 0)
        {
            var search = model.ShowOfflineJournal ? OfflineJournalSearch : WorkflowSearchInput;
            search.Focus(); search.SelectAll(); e.Handled = true;
        }
    }
    private void OnClearSearch(object? sender, RoutedEventArgs e)
    {
        Model?.ClearSearchCommand.Execute(null);
        WorkflowSearchInput.Focus();
    }
    private void OnOrderFiltersOpened(object? sender, EventArgs e)
    {
        if (sender is Flyout { Content: Control content }) content.DataContext = Model;
    }
    private void OnClearOrderFilters(object? sender, RoutedEventArgs e) => WorkflowOrderFiltersButton.Focus();
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
    private void ApplyTheme()
    {
        var theme = ThemeSwitch.IsChecked == true ? ThemeVariant.Dark : ThemeVariant.Light;
        RequestedThemeVariant = theme;
        if (Avalonia.Application.Current is { } app) app.RequestedThemeVariant = theme;
    }
    private void OnOpenOrder(object? sender, TappedEventArgs e) => Model?.OpenOrderCommand.Execute(null);
    private async void OnChooseCommentTemplates(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanUse: true, SelectedDraft: { } draft } model || OwnedWindows.Count > 0) return;
        var templates = WorkflowViewModel.CommentTemplateLines(model.CommentTemplatesText);
        var picker = new WorkflowCommentTemplatePicker(templates, draft.Comment) { RequestedThemeVariant = ActualThemeVariant };
        var changes = await picker.ShowDialog<WorkflowCommentTemplateChanges?>(this);
        if (!IsVisible || !ReferenceEquals(Model, model) || !ReferenceEquals(model.SelectedDraft, draft) || !model.CanUse) return;

        if (changes is not null)
            draft.Comment = WorkflowViewModel.ApplyCommentTemplateChanges(draft.Comment, changes);
        WorkflowSpecificationComment.Focus();
        if (changes is { Added.Length: > 0 })
            WorkflowSpecificationComment.CaretIndex = WorkflowSpecificationComment.Text?.Length ?? 0;
    }
    private void OnClearComment(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanUse: true, SelectedDraft: { } draft }) return;
        draft.ClearComment();
        WorkflowSpecificationComment.Focus();
    }
    private void OnUndoClearComment(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanUse: true, SelectedDraft: { } draft }) return;
        draft.UndoClearComment();
        WorkflowSpecificationComment.Focus();
        WorkflowSpecificationComment.CaretIndex = WorkflowSpecificationComment.Text?.Length ?? 0;
    }
    private void OnFocusNewOrder(object? sender, RoutedEventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (IsVisible && Model is { IsCreating: true, CanUse: true } model)
            NewManagerInput.Focus();
    });
    private void OnFocusProject(object? sender, RoutedEventArgs e) => Dispatcher.UIThread.Post(() =>
    { if (IsVisible && Model is { IsPlanning: true, CanUse: true }) WorkflowProjectThickness.Focus(); });
    private async void OnCreateOrder(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanCreateOrder: true } model) return;
        await model.CreateOrderCommand.ExecuteAsync(null);
        if (IsVisible && !model.ShowOfflineJournal && !model.IsCreating && !model.HasQueuedOfficeOrders && model.IsOrderOpen && model.Error is null)
        { WorkflowOrderTabs.SelectedIndex = 0; Dispatcher.UIThread.Post(() => { if (IsVisible) PrepareWorkflowProject.Focus(); }); }
    }
    private async void OnSettings(object? sender, RoutedEventArgs e) => await ShowSettingsAsync(0);
    private async void OnReviewQueuedOrder(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: WorkflowQueuedOrderRow row } && Model is { } model)
            await model.ReviewQueuedOrderAsync(row, _lifetime.Token);
    }
    private async void OnOpenLastOfficeCreation(object? sender, RoutedEventArgs e)
    {
        if (Model is { } model) await model.OpenLastOfficeCreationAsync(_lifetime.Token);
    }
    private async void OnProjectStorageSettings(object? sender, RoutedEventArgs e) => await ShowSettingsAsync(1);
    private async Task ShowSettingsAsync(int section)
    {
        if (Model is not { } model) return;
        if (OwnedWindows.OfType<WorkflowSettingsWindow>().FirstOrDefault() is { } existing)
        { if (existing.OwnedWindows.Count == 0) { existing.SelectSection(section); existing.Activate(); } return; }
        if (OwnedWindows.OfType<WorkflowUpdatesWindow>().FirstOrDefault() is { } updates)
        { if (!updates.CanClose) return; updates.Close(); }
        if (OwnedWindows.Count > 0 || !model.CanUse && section != 2) return;
        var window = new WorkflowSettingsWindow { DataContext = model.CreateGeneralSettings(), RequestedThemeVariant = ActualThemeVariant };
        window.SelectSection(section);
        await window.ShowDialog(this);
        RefreshOfficeWorkspace();
    }
    private async void OnMoveToTrash(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await ShowOrderTrashConfirmationAsync();
    }
    private async Task ShowOrderTrashConfirmationAsync(bool returnToJournal = false)
    {
        if (Model is not { CanUse: true } model || OwnedWindows.Count > 0 || model.CreateTrashConfirmation() is not { } confirmation) return;
        var window = new WorkflowTrashConfirmationWindow { DataContext = confirmation, RequestedThemeVariant = ActualThemeVariant };
        await window.ShowDialog(this);
        if (!IsVisible) return;
        if (returnToJournal) WorkflowOrdersGrid.Focus();
        else if (WorkflowOrderActions.IsEffectivelyVisible) WorkflowOrderActions.Focus();
        else WorkflowOrdersGrid.Focus();
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
    private async void OnUpdates(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        if (OwnedWindows.OfType<WorkflowSettingsWindow>().FirstOrDefault() is { } settings)
        { if (settings.OwnedWindows.Count == 0) { settings.SelectSection(2); settings.Activate(); } return; }
        if (OwnedWindows.OfType<WorkflowUpdatesWindow>().FirstOrDefault() is { } existing)
        { existing.Activate(); return; }
        if (OwnedWindows.Count > 0) return;
        var window = new WorkflowUpdatesWindow { DataContext = model.Updates, RequestedThemeVariant = ActualThemeVariant };
        await window.ShowDialog(this);
    }
    private async void OnCopyResult(object? sender, RoutedEventArgs e)
    {
        if (Model is { CanCopyResult: true } model && OwnedWindows.Count == 0)
            await model.CopyResultAsync(new WindowResultClipboard(WriteResultClipboardAsync));
    }
    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Model is not { CanUse: true } model) return;
        try
        {
            var path = (sender as Control)?.DataContext switch
            {
                WorkflowProjectRow row when model.IsRemoteStorage => await OpenOfficeWorkingFolderAsync(model, row),
                WorkflowProjectRow row => row.FolderPath,
                SpecificationDraft draft when !model.IsRemoteStorage => draft.FolderPath,
                _ => null
            };
            if (string.IsNullOrWhiteSpace(path)) return;
            if (!await OpenFolderAsync(path) && IsVisible && !_lifetime.IsCancellationRequested)
                model.Error = "Рабочая папка недоступна.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (IsVisible && !_lifetime.IsCancellationRequested) model.Error = "Рабочая папка недоступна."; }
    }

    private void OnOfficeWorkspaceActivated(object? sender, EventArgs e) => RefreshOfficeWorkspace(recentlyOpenedOnly: true);

    private void RefreshOfficeWorkspace(bool recentlyOpenedOnly = false)
    {
        if (IsVisible && !_lifetime.IsCancellationRequested && !_closeDialogPending && OwnedWindows.Count == 0 &&
            Model is { HasOfficeWorkingCopies: true, CanUse: true } model)
            _ = model.RefreshOfficePendingFilesAsync(_lifetime.Token, recentlyOpenedOnly);
    }

    private async Task<string?> OpenOfficeWorkingFolderAsync(WorkflowViewModel model, WorkflowProjectRow row)
    {
        var token = _lifetime.Token;
        bool Current() => IsVisible && ReferenceEquals(Model, model) && !token.IsCancellationRequested && model.Projects.Contains(row);
        while (Current() && await model.OpenOfficeProjectAsync(row, token) is { } copy && Current())
        {
            if (copy.Conflicts.Count == 0) return copy.Folder;
            foreach (var conflict in copy.Conflicts)
                if (!await ResolveOfficeWorkingConflictAsync(model, row, conflict, Current)) return null;
        }
        return null;
    }
    private async Task<bool> ResolveOfficeWorkingConflictAsync(WorkflowViewModel model, WorkflowProjectRow row,
        WorkflowOfficeFileConflict conflict, Func<bool> isCurrent, bool forShopTransfer = false)
    {
        var token = _lifetime.Token;
        var comparison = await model.CompareOfficeWorkingFileAsync(row, conflict, token, refreshVersions: forShopTransfer);
        if (comparison is null || !isCurrent()) return false;
        var versions = new WorkflowOfficeFileConflict(conflict.Path, comparison.HomeHash, comparison.OfficeHash);
        var dialog = new WorkflowOfficeReportComparisonWindow(versions, () => OpenFolderAsync(comparison.Folder), forShopTransfer)
            { RequestedThemeVariant = ActualThemeVariant };
        var choice = await dialog.ShowDialog<WorkflowOfficeReportChoice>(this);
        return choice != WorkflowOfficeReportChoice.Cancel && isCurrent() &&
            await model.ChooseOfficeWorkingFileAsync(row, versions, comparison, choice == WorkflowOfficeReportChoice.Office, token);
    }
    private async void OnPublishOfficeProject(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanUse: true } model || (sender as Control)?.DataContext is not WorkflowProjectRow row) return;
        var result = await model.PublishOfficeProjectAsync(row, _lifetime.Token);
        if (result is null || !IsVisible || _lifetime.IsCancellationRequested) return;
        var status = result is { Sent: 0, Problems.Length: 0, StopReason: null } ? "Изменённых файлов для отправки нет." : $"Отправлено файлов: {result.Sent}.";
        var details = string.Join("\n", new[] { row.Name }.Concat(result.Problems).Append(result.StopReason).OfType<string>());
        await new WorkflowProjectActionWindow("Файлы проекта", status, details, "Закрыть", cancel: "")
            { RequestedThemeVariant = ActualThemeVariant }.ShowDialog(this);
    }
    protected virtual async Task<bool> OpenFolderAsync(string path)
    {
        var folder = await StorageProvider.TryGetFolderFromPathAsync(path);
        return folder is not null && IsVisible && !_lifetime.IsCancellationRequested &&
            await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
    }
    private async void OnRefreshReports(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanLoadReports: true } model || OwnedWindows.Count > 0) return;
        var review = await model.CreateReportReviewAsync(_lifetime.Token);
        if (review is null || !IsVisible || _lifetime.IsCancellationRequested) return;
        await ShowReportReviewAsync(model, review);
    }
    private async Task ShowReportReviewAsync(WorkflowViewModel model, WorkflowReportReviewViewModel review)
    {
        var window = new WorkflowReportReviewWindow(candidate =>
        {
            var catalog = model.CreateCatalogSettings();
            catalog.SelectTariff(candidate.Project.MachineId, candidate.Draft!.MaterialGroup, candidate.Project.ThicknessMm);
            return new(() => model.CreateDatabaseSettings()) { DataContext = catalog };
        })
        { DataContext = review, RequestedThemeVariant = ActualThemeVariant };
        if (review.IsGuided) { window.Width = 1120; window.Height = 700; }
        await window.ShowDialog(this);
        model.ReportReviewClosed();
    }
    private void OnGoToProject(object? sender, RoutedEventArgs e)
    {
        if (Model?.SelectedProject is not { } project) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible && ReferenceEquals(Model?.SelectedProject, project)) WorkflowProjectsGrid.ScrollIntoView(project, null);
        }, DispatcherPriority.Loaded);
    }
    private async void OnOpenSpecification(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanUse: true, SpecificationSourcePath: { } path } model) return;
        var token = _lifetime.Token;
        var draft = model.SelectedDraft;
        var archive = model.IsRemoteStorage ? null : draft?.Saved?.Report;
        bool Current() => !token.IsCancellationRequested && IsVisible && ReferenceEquals(Model, model) &&
            ReferenceEquals(draft, model.SelectedDraft) && model.SpecificationSourcePath == path;
        model.Error = null;
        try
        {
            // The file may have been replaced since calculation. Open the current source.
            var local = await model.ResolveSpecificationSourceAsync(path, token);
            if (local is null) return;
            var attributes = await Task.Run(() => File.GetAttributes(local), token);
            if (attributes.HasFlag(FileAttributes.Directory)) throw new FileNotFoundException();
            if (archive is not null)
            {
                string? warning;
                try
                {
                    var same = await Task.Run(async () => new FileInfo(local).Length == archive.Length &&
                        (await WorkflowOfficeProjectPaths.FingerprintAsync(local, token)).Hash.Equals(archive.Sha256, StringComparison.OrdinalIgnoreCase), token);
                    warning = same ? null : "Рабочий файл отличается от спецификации, по которой сохранён расчёт.";
                }
                catch (Exception ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException &&
                    ex is IOException or UnauthorizedAccessException or WorkflowValidationException)
                { warning = "Не удалось сверить рабочий файл с версией расчёта."; }
                if (!Current()) return;
                model.SpecificationSourceWarning = warning;
            }
            if (Current() && !await LaunchSpecificationAsync(local))
                model.Error = "Не удалось открыть рабочую спецификацию. Проверьте приложение для PDF или HTML.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!Current()) return;
            if (archive is not null && ex is FileNotFoundException or DirectoryNotFoundException)
                model.SpecificationSourceWarning = "Рабочая спецификация не найдена. Можно открыть сохранённую версию расчёта.";
            model.Error = ex switch
            {
                FileNotFoundException or DirectoryNotFoundException => "Рабочая спецификация не найдена. Проверьте файл в папке проекта.",
                UnauthorizedAccessException or IOException => "Рабочая спецификация недоступна. Проверьте доступ к папке проекта.",
                _ => "Не удалось открыть рабочую спецификацию. Проверьте приложение для PDF или HTML."
            };
        }
    }

    private async void OnOpenCalculationArchive(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanUse: true, CalculationArchiveToOpen: { } archive } model) return;
        var token = _lifetime.Token;
        bool Current() => IsVisible && !token.IsCancellationRequested && ReferenceEquals(Model, model) && model.CalculationArchiveToOpen == archive;
        model.Error = null;
        try
        {
            var path = await Task.Run(() => WorkflowReportFiles.PrepareArchiveForOpeningAsync(archive, token), token);
            if (Current() && !await LaunchSpecificationAsync(path))
                model.Error = "Не удалось открыть сохранённую спецификацию. Проверьте приложение для HTML/PDF.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkflowValidationException)
        { if (Current()) model.Error = "Сохранённая спецификация недоступна или повреждена."; }
    }
    protected virtual Task<bool> LaunchSpecificationAsync(string path) => Launcher.LaunchFileInfoAsync(new FileInfo(path));
}
