using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Jornal.Desktop.ViewModels;
using Jornal.Infrastructure.Local;

namespace Jornal.Desktop.Controls.Settings;

public partial class DatabaseSettingsPanel : UserControl
{
    private bool _stopped;
    private readonly CancellationTokenSource _importCancellation = new();
    public DatabaseSettingsPanel()
    {
        InitializeComponent();
    }
    private DatabaseSettingsViewModel? Model => DataContext as DatabaseSettingsViewModel;
    private bool IsOpen => TopLevel.GetTopLevel(this) is Window { IsVisible: true };
    public void FocusInitial()
    {
        if (Model?.UseLocalCopy == true) LocalCopyPathInput.Focus();
        else if (Model?.UseDatabase == true) DatabasePathInput.Focus();
        else DatabaseModeButton.Focus();
    }
    public void CancelPending()
    {
        if (_stopped) return;
        _stopped = true;
        Model?.ConnectCommand.Cancel();
        _importCancellation.Cancel();
    }
    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        CancelPending();
        if (Model?.IsImporting != true) _importCancellation.Dispose();
        base.OnDetachedFromVisualTree(e);
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model || !TopLevel.GetTopLevel(this)!.StorageProvider.CanOpen) return;
        try
        {
            var files = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Выберите базу OrderDB.mdb", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("База Access (*.mdb)") { Patterns = ["*.mdb", "*.MDB"] }]
            });
            if (files.Count > 0 && IsOpen)
            {
                var path = files[0].TryGetLocalPath();
                if (path is null) model.Error = "Выберите файл в локальной или подключённой сетевой папке.";
                else { model.UseDatabase = true; model.DatabasePath = path; }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            model.Error = "Не удалось открыть выбор файла. Можно ввести полный путь вручную.";
        }
    }

    private static FilePickerFileType LocalCopyType => new("Локальная тестовая копия") { Patterns = ["*" + LocalJournalStore.Extension] };

    private async void OnBrowseLocalCopyClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanUseLocalCopy: true } model || !TopLevel.GetTopLevel(this)!.StorageProvider.CanOpen) return;
        try
        {
            var files = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                { Title = "Выберите локальную копию", AllowMultiple = false, FileTypeFilter = [LocalCopyType] });
            if (files.Count > 0 && IsOpen && files[0].TryGetLocalPath() is { } path)
            { model.UseLocalCopy = true; model.LocalCopyPath = path; }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { model.Error = "Не удалось открыть выбор файла. Можно ввести полный путь вручную."; }
    }

    private async void OnImportLocalCopyClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanUseLocalCopy: true, CanChangeSource: true } model || !TopLevel.GetTopLevel(this)!.StorageProvider.CanOpen || !TopLevel.GetTopLevel(this)!.StorageProvider.CanSave) return;
        try
        {
            var sources = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Выберите закрытую копию исходной MDB", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("База Access (*.mdb)") { Patterns = ["*.mdb", "*.MDB"] }]
            });
            if (!IsOpen || sources.Count == 0 || sources[0].TryGetLocalPath() is not { } source) return;
            var destination = await TopLevel.GetTopLevel(this)!.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Сохраните новую локальную тестовую копию",
                SuggestedFileName = "OrderDB-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + LocalJournalStore.Extension,
                FileTypeChoices = [LocalCopyType], ShowOverwritePrompt = true
            });
            if (IsOpen && destination?.TryGetLocalPath() is { } path)
                await model.ImportLocalCopyAsync(source, path, _importCancellation.Token);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { if (IsOpen) model.Error = "Не удалось открыть выбор файла. Можно ввести полный путь вручную."; }
        finally { if (!IsOpen) _importCancellation.Dispose(); }
    }
}
