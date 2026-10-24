using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using System.ComponentModel;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowSettingsWindow : Window
{
    private bool _discard;
    private WorkflowSettingsViewModel? _observed;
    private WorkflowSettingsViewModel? Model => DataContext as WorkflowSettingsViewModel;
    public bool CanRestart => OwnedWindows.Count == 0 && Model is { CanRestart: true };
    public WorkflowSettingsWindow() => InitializeComponent();
    public void SelectSection(int section)
    { if (Model is { CanEdit: true } model) model.SelectedSectionIndex = section; }
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e); _observed = Model;
        if (_observed is not null) _observed.Journal.PropertyChanged += OnJournalChanged;
        UpdateTheme();
    }
    private void OnJournalChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(WorkflowViewModel.IsDarkTheme)) UpdateTheme(); }
    private void UpdateTheme() => RequestedThemeVariant = Model?.Journal.IsDarkTheme == true ? ThemeVariant.Dark : ThemeVariant.Light;
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (OwnedWindows.Count > 0 || Model is { CanClose: false }) e.Cancel = true;
        else if (!_discard && Model is { HasChanges: true } model)
        { e.Cancel = true; model.Error = "Сохраните изменения или нажмите «Отмена»."; }
        base.OnClosing(e);
    }
    private void OnClose(object? sender, RoutedEventArgs e)
    { if (OwnedWindows.Count == 0 && Model is { CanClose: true }) { _discard = true; Close(); } }
    private async void OnEntrySuggestions(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanEdit: true } model || OwnedWindows.Count > 0) return;
        var window = new WorkflowEntrySuggestionsWindow { DataContext = model.CreateEntrySuggestionsEditor(), RequestedThemeVariant = ActualThemeVariant };
        await window.ShowDialog(this);
        model.RefreshEntrySuggestionsSummary();
        if (IsVisible) WorkflowEntrySuggestionsButton.Focus();
    }
    protected override void OnClosed(EventArgs e)
    {
        ChangeWorkflowProfileColor.Flyout?.Hide();
        if (_observed is not null) { _observed.Journal.PropertyChanged -= OnJournalChanged; _observed.Dispose(); _observed = null; }
        base.OnClosed(e);
    }
    private async void OnOpenWorkflowReleases(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanEdit: true } model || WorkflowIdentity.UpdatesRepository is not { } repository) return;
        try
        {
            if (!await Launcher.LaunchUriAsync(WorkflowIdentity.ReleasesPageFor(repository)))
                model.Error = $"Не удалось открыть выпуски {WorkflowIdentity.Name}.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { model.Error = $"Не удалось открыть выпуски {WorkflowIdentity.Name}."; }
    }
    private async void OnOpenWorkflowDiagnostics(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanEdit: true } model) return;
        try
        {
            if (!Directory.Exists(model.DiagnosticsDirectory) || !await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(model.DiagnosticsDirectory)))
                model.DiagnosticsStatus = $"Папка диагностики {WorkflowIdentity.Name} недоступна.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { model.DiagnosticsStatus = $"Не удалось открыть папку диагностики {WorkflowIdentity.Name}."; }
    }
    private async void OnExportWorkflowDiagnostics(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanExportDiagnostics: true } model || !StorageProvider.CanSave) return;
        await model.ExportDiagnosticsAsync(async token =>
        {
            var file = await StorageProvider.SaveFilePickerAsync(new()
            {
                Title = $"Сохранить диагностический отчёт {WorkflowIdentity.Name}", SuggestedFileName = $"Journal-Workflow-report-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
                DefaultExtension = "zip", FileTypeChoices = [new FilePickerFileType("Архив ZIP") { Patterns = ["*.zip"] }]
            });
            token.ThrowIfCancellationRequested();
            return file is null ? null : await file.OpenWriteAsync();
        });
    }
    private void OnProfileColorMenuOpened(object? sender, EventArgs e) => ShowColorPalette();
    private void OnProfileColorMenuClosed(object? sender, EventArgs e) => ShowColorPalette();
    private void ShowColorPalette()
    { WorkflowProfilePalette.IsVisible = true; WorkflowPreciseColorMenu.IsVisible = false; }
    private void OnShowPreciseColor(object? sender, RoutedEventArgs e)
    { WorkflowProfilePalette.IsVisible = false; WorkflowPreciseColorMenu.IsVisible = true; WorkflowProfileColor.Focus(); e.Handled = true; }
    private void OnShowColorPalette(object? sender, RoutedEventArgs e)
    { ShowColorPalette(); MoreWorkflowProfileColors.Focus(); e.Handled = true; }
    private void OnConfirmProfileColor(object? sender, RoutedEventArgs e)
    { CloseColorMenu(); e.Handled = true; }
    private void CloseColorMenu()
    {
        ChangeWorkflowProfileColor.Flyout?.Hide();
        if (ChangeWorkflowProfileColor.IsEffectivelyVisible && ChangeWorkflowProfileColor.IsEffectivelyEnabled) ChangeWorkflowProfileColor.Focus();
    }
    private void OnProfileColorMenuKeyDown(object? sender, KeyEventArgs e)
    { if (e.Key == Key.Escape) { CloseColorMenu(); e.Handled = true; } }
    private void OnSelectProfileColor(object? sender, RoutedEventArgs e)
    {
        if (Model is { CanEdit: true } model && (sender as Control)?.DataContext is WorkflowProfileColorOption color)
        { model.SelectProfileColorCommand.Execute(color); CloseColorMenu(); }
        e.Handled = true;
    }
    private async void OnBrowseProjects(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanEdit: true } model || !StorageProvider.CanPickFolder) return;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Общая папка проектов", AllowMultiple = false });
            if (IsVisible && folders.FirstOrDefault()?.TryGetLocalPath() is { } path) model.ProjectRoot = path;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { model.Error = "Не удалось открыть выбор папки."; }
    }
}
