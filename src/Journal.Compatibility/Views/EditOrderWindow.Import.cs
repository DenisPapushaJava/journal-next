using System.ComponentModel;
using Avalonia.Input;
using Jornal.Desktop.ViewModels;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Jornal.Parsing.Specifications;

namespace Jornal.Desktop.Views;

public partial class EditOrderWindow
{
    private readonly CancellationTokenSource _importLifetime = new();
    private int _fileDragGeneration;
    private IInputElement? _focusBeforeImport;
    private void OnImportStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(EditOrderViewModel.HasPendingImport) or nameof(EditOrderViewModel.IsImporting)) || _viewModel is not { IsImporting: false } model) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible || !ReferenceEquals(model, _viewModel) || model.IsImporting) return;
            if (model.HasPendingImport) ReplaceImportButton.Focus();
            else { var focus = _focusBeforeImport; _focusBeforeImport = null; focus?.Focus(); }
        });
    }
    private void ConfigureFileDrop()
    {
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnFileDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragOverEvent, OnFileDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, OnFileDragLeave, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnFileDrop, RoutingStrategies.Bubble, handledEventsToo: true);
    }
    private static IReadOnlyList<string>? DroppedFiles(DragEventArgs e) => !e.DataTransfer.Contains(DataFormat.File) ? null :
        e.DataTransfer.TryGetFiles()?.Select(file => file is IStorageFile ? file.TryGetLocalPath() ?? "" : "").ToArray() ?? [];
    private void OnFileDragOver(object? sender, DragEventArgs e)
    {
        if (_viewModel is not { } model || DroppedFiles(e) is not { } paths) return;
        _fileDragGeneration++; e.Handled = true;
        model.DropRejection = model.CheckFileDrop(paths); model.IsDraggingFiles = true;
        e.DragEffects = model.DropRejection is null ? DragDropEffects.Copy : DragDropEffects.None;
    }
    private void OnFileDragLeave(object? sender, DragEventArgs e)
    {
        var generation = ++_fileDragGeneration;
        Dispatcher.UIThread.Post(() => { if (generation == _fileDragGeneration && _viewModel is { } model) model.IsDraggingFiles = false; }, DispatcherPriority.Background);
    }
    private async void OnFileDrop(object? sender, DragEventArgs e)
    {
        if (DroppedFiles(e) is not { } paths) return;
        _fileDragGeneration++; e.Handled = true; e.DragEffects = DragDropEffects.None;
        if (IsVisible && OwnedWindows.Count == 0 && _viewModel is { } model && model.CheckFileDrop(paths) is null)
            Activate();
        await ImportSpecificationsAsync(paths);
    }
    public async Task ImportSpecificationsAsync(IReadOnlyList<string> paths)
    {
        if (_viewModel is not { } model) return;
        model.IsDraggingFiles = false;
        if (!IsVisible) return;
        if (model.CanImport) _focusBeforeImport = FocusManager?.GetFocusedElement();
        await model.RequestImportAsync(paths, _importLifetime.Token);
    }
    private void OnCancelFormImport(object? sender, RoutedEventArgs e) => _viewModel?.CancelImportRead();
    private async void OnBrowseSpecification(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not { CanImport: true } model || !StorageProvider.CanOpen) return;
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Заменить спецификацию", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Спецификации") { Patterns = SpecificationProfileCatalog.Default.SupportedExtensions
                    .SelectMany(extension => new[] { "*" + extension, "*" + extension.ToUpperInvariant() }).ToArray() }]
            });
            if (IsVisible && files.Count > 0)
                await ImportSpecificationsAsync(files.Select(file => file.TryGetLocalPath() ?? "").ToArray());
        }
        catch (OperationCanceledException) when (_importLifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { if (IsVisible) model.ImportError = "Не удалось открыть выбор спецификации."; }
    }
}
