using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowSettingsWindow : Window
{
    private bool _discard;
    private WorkflowSettingsViewModel? _observed;
    private readonly AsyncRelayCommand<string> _removeTechnologist;
    private WorkflowSettingsViewModel? Model => DataContext as WorkflowSettingsViewModel;
    public bool CanRestart => OwnedWindows.Count == 0 && Model is { CanRestart: true };
    public WorkflowSettingsWindow()
    {
        InitializeComponent();
        _removeTechnologist = new(OnRemoveTechnologistAsync, name => Model?.CanRemoveTechnologist(name) == true);
        WorkflowProfileName.RemoveSuggestionCommand = _removeTechnologist;
    }
    public void SelectSection(int section)
    { if (Model is { CanEdit: true } model) model.SelectedSectionIndex = section; }
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e); _observed = Model;
        if (_observed is not null)
        { _observed.Journal.PropertyChanged += OnJournalChanged; _observed.PropertyChanged += OnSettingsChanged; }
        UpdateTheme();
    }
    private void OnJournalChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(WorkflowViewModel.IsDarkTheme)) UpdateTheme(); _removeTechnologist.NotifyCanExecuteChanged(); }
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) => _removeTechnologist.NotifyCanExecuteChanged();
    private async Task OnRemoveTechnologistAsync(string? name)
    {
        if (Model is not { } model || OwnedWindows.Count > 0 || !model.CanRemoveTechnologist(name) || model.FindTechnologist(name) is not { } employee) return;
        var dialog = new WorkflowProjectActionWindow("Убрать технолога из списка?",
            "Технолог исчезнет из списка выбора. Заявки, расчёты и авторство сохранятся. Чтобы вернуть его, введите эту фамилию в настройках и сохраните профиль.",
            employee.Name, "Убрать из списка") { RequestedThemeVariant = ActualThemeVariant };
        if (await dialog.ShowDialog<bool>(this)) await model.RemoveTechnologistAsync(employee);
        if (IsVisible) Dispatcher.UIThread.Post(() =>
        { if (IsVisible) WorkflowProfileName.GetVisualDescendants().OfType<TextBox>().FirstOrDefault()?.Focus(); });
    }
    private void UpdateTheme() => RequestedThemeVariant = Model?.Journal.IsDarkTheme == true ? ThemeVariant.Dark : ThemeVariant.Light;
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        Model?.NormalizeResultPriceInput();
        if (OwnedWindows.Count > 0 || Model is { CanClose: false }) e.Cancel = true;
        else if (!_discard && Model is { HasChanges: true } model)
        { e.Cancel = true; model.Error = "Сохраните изменения или нажмите «Отмена»."; }
        base.OnClosing(e);
    }
    private void OnResultMinimumLostFocus(object? sender, RoutedEventArgs e) => Model?.NormalizeResultPriceInput();
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
        if (_observed is not null)
        { _observed.Journal.PropertyChanged -= OnJournalChanged; _observed.PropertyChanged -= OnSettingsChanged; _observed.Dispose(); _observed = null; }
        base.OnClosed(e);
    }
    private async void OnOpenWorkflowReleases(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanEdit: true } model || WorkflowIdentity.UpdatesRepository is not { } repository) return;
        try
        {
            if (!await Launcher.LaunchUriAsync(WorkflowIdentity.ReleasesPageFor(repository)))
                model.Error = $"Не удалось открыть выпуски программы «{WorkflowIdentity.Name}».";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { model.Error = $"Не удалось открыть выпуски программы «{WorkflowIdentity.Name}»."; }
    }
    private async void OnOpenWorkflowDiagnostics(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanEdit: true } model) return;
        try
        {
            if (!Directory.Exists(model.DiagnosticsDirectory) || !await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(model.DiagnosticsDirectory)))
                model.DiagnosticsStatus = $"Папка диагностики программы «{WorkflowIdentity.Name}» недоступна.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { model.DiagnosticsStatus = $"Не удалось открыть папку диагностики программы «{WorkflowIdentity.Name}»."; }
    }
    private async void OnExportWorkflowDiagnostics(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanExportDiagnostics: true } model || !StorageProvider.CanSave) return;
        await model.ExportDiagnosticsAsync(async token =>
        {
            var file = await StorageProvider.SaveFilePickerAsync(new()
            {
                Title = $"Сохранить диагностический отчёт программы «{WorkflowIdentity.Name}»", SuggestedFileName = $"Journal-Workflow-report-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
                DefaultExtension = "zip", FileTypeChoices = [new FilePickerFileType("Архив ZIP") { Patterns = ["*.zip"] }]
            });
            token.ThrowIfCancellationRequested();
            return file is null ? null : await file.OpenWriteAsync();
        });
    }
    private Button? _colorMenuAnchor;
    private void OnProfileColorClick(object? sender, RoutedEventArgs e)
    { _colorMenuAnchor = ChangeWorkflowProfileColor; Model?.BeginColorPicker(false); }
    private void OnShopColorClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanEdit: true } model) return;
        _colorMenuAnchor = ChangeWorkflowShopColor; model.BeginColorPicker(true);
        ChangeWorkflowProfileColor.Flyout?.ShowAt(ChangeWorkflowShopColor);
    }
    private void OnProfileColorMenuOpened(object? sender, EventArgs e) => ShowColorPalette();
    private void OnProfileColorMenuClosed(object? sender, EventArgs e)
    { ShowColorPalette(); Model?.BeginColorPicker(false); }
    private void ShowColorPalette()
    { WorkflowProfilePalette.IsVisible = true; WorkflowPreciseColorMenu.IsVisible = false; }
    private void OnShowPreciseColor(object? sender, RoutedEventArgs e)
    { WorkflowProfilePalette.IsVisible = false; WorkflowPreciseColorMenu.IsVisible = true; WorkflowProfileColor.Focus(); e.Handled = true; }
    private void OnShowColorPalette(object? sender, RoutedEventArgs e)
    { ShowColorPalette(); MoreWorkflowProfileColors.Focus(); e.Handled = true; }
    private void OnConfirmProfileColor(object? sender, RoutedEventArgs e)
    { if (Model is not { IsEditingShopColor: true } model || model.CommitShopColor()) CloseColorMenu(); e.Handled = true; }
    private void CloseColorMenu()
    {
        var anchor = _colorMenuAnchor ?? ChangeWorkflowProfileColor;
        ChangeWorkflowProfileColor.Flyout?.Hide();
        if (anchor.IsEffectivelyVisible && anchor.IsEffectivelyEnabled) anchor.Focus();
    }
    private void OnProfileColorMenuKeyDown(object? sender, KeyEventArgs e)
    { if (e.Key == Key.Escape) { CloseColorMenu(); e.Handled = true; } }
    private void OnSelectProfileColor(object? sender, RoutedEventArgs e)
    {
        if (Model is { CanEdit: true } model && (sender as Control)?.DataContext is WorkflowProfileColorOption color)
        {
            if (model.IsEditingShopColor)
            { model.PickerSpectrumColor = new(Avalonia.Media.Color.FromUInt32(color.Argb)); if (model.CommitShopColor()) CloseColorMenu(); }
            else { model.SelectProfileColorCommand.Execute(color); CloseColorMenu(); }
        }
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
    private async void OnBrowseShopRoot(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanEdit: true } model || !StorageProvider.CanPickFolder) return;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Папка заданий в цех", AllowMultiple = false });
            if (IsVisible && folders.FirstOrDefault()?.TryGetLocalPath() is { } path) model.ShopRoot = path;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { model.Error = "Не удалось открыть выбор папки цеха. Можно ввести полный путь вручную."; }
    }
    private async void OnConnectDatabase(object? sender, RoutedEventArgs e) => await ChooseDatabaseAsync(false);
    private async void OnCreateDatabase(object? sender, RoutedEventArgs e) => await ChooseDatabaseAsync(true);
    private async Task ChooseDatabaseAsync(bool create)
    {
        if (Model is not { CanChooseDatabase: true } model || OwnedWindows.Count > 0) return;
        if (model.HasChanges) { model.Error = "Сначала сохраните изменения настроек или закройте их кнопкой «Отмена»."; return; }
        try
        {
            string? path;
            var type = new FilePickerFileType($"База программы «{WorkflowIdentity.Name}» (*.mdb)") { Patterns = ["*.mdb"] };
            if (create)
            {
                if (!StorageProvider.CanSave) return;
                var file = await StorageProvider.SaveFilePickerAsync(new()
                { Title = $"Создать базу программы «{WorkflowIdentity.Name}»", SuggestedFileName = "Заявки.mdb", DefaultExtension = "mdb", FileTypeChoices = [type], ShowOverwritePrompt = true });
                path = file?.TryGetLocalPath();
            }
            else
            {
                if (!StorageProvider.CanOpen) return;
                var files = await StorageProvider.OpenFilePickerAsync(new()
                { Title = $"Подключить базу программы «{WorkflowIdentity.Name}»", AllowMultiple = false, FileTypeFilter = [type] });
                path = files.FirstOrDefault()?.TryGetLocalPath();
            }
            if (IsVisible && path is not null) await model.ChooseDatabaseAsync(path, create);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { model.Error = "Не удалось выбрать файл MDB. Прежнее подключение сохранено."; }
    }
}
