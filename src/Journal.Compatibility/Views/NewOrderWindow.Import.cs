using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Jornal.Parsing.Specifications;

namespace Jornal.Desktop.Views;

public partial class NewOrderWindow
{
    private readonly CancellationTokenSource _importLifetime = new();
    private void OnCancelFormImport(object? sender, RoutedEventArgs e) => _viewModel?.CancelImportRead();
    private async void OnBrowseSpecification(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not { CanAddFiles: true } model || !StorageProvider.CanOpen) return;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Выберите спецификации", AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType("Спецификации") { Patterns = SpecificationProfileCatalog.Default.SupportedExtensions
                    .SelectMany(extension => new[] { "*" + extension, "*" + extension.ToUpperInvariant() }).ToArray() }]
            });
            if (!IsVisible || files.Count == 0) return;
            await model.ImportFilesAsync(files.Select(file => file.TryGetLocalPath() ?? "").ToArray(), _importLifetime.Token);
        }
        catch (OperationCanceledException) when (_importLifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { if (IsVisible) model.ImportError = "Не удалось открыть выбор спецификации. " + exception.Message; }
    }
}
