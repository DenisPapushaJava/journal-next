using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowWindow
{
    private bool _pickingCalculationPackage;

    private async void OnExportCalculationDrafts(object? sender, RoutedEventArgs e)
    {
        if (Model is { CanUse: true } model) await model.ExportCalculationDraftsAsync(_lifetime.Token);
    }

    private async void OnImportCalculationPackage(object? sender, RoutedEventArgs e)
    {
        if (_pickingCalculationPackage || OwnedWindows.Count > 0 || !StorageProvider.CanOpen ||
            Model is not { CanUse: true, CalculationExchangeRoot: { } folder } model) return;
        _pickingCalculationPackage = true;
        try
        {
            var selected = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Принять расчёт из папки обмена",
                SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(folder),
                AllowMultiple = false,
                FileTypeFilter = [new("Пакет расчёта") { Patterns = ["*" + WorkflowCalculationExchange.Extension] }]
            });
            if (IsVisible && ReferenceEquals(Model, model) && selected.SingleOrDefault()?.TryGetLocalPath() is { } path)
                await model.ImportCalculationPackageAsync(path, _lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { model.Error = "Не удалось открыть папку обмена. Проверьте путь в настройках."; }
        finally { _pickingCalculationPackage = false; }
    }
}
