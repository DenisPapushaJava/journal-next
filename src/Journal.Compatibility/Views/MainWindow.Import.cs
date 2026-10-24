using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Jornal.Desktop.Import;

namespace Jornal.Desktop.Views;

public partial class MainWindow
{
    private CancellationTokenSource? _specificationImportCancellation;
    private IInputElement? _focusBeforeSpecification;
    private int _specificationDragGeneration;
    public ISpecificationImportService SpecificationImporter { get; set; } = new SpecificationImportService();

    private void ConfigureSpecificationDrop()
    {
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnSpecificationDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragOverEvent, OnSpecificationDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, OnSpecificationDragLeave, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnSpecificationDrop, RoutingStrategies.Bubble, handledEventsToo: true);
    }
    private static IReadOnlyList<string>? DroppedFiles(DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File)) return null;
        return e.DataTransfer.TryGetFiles()?.Select(file => file is IStorageFile ? file.TryGetLocalPath() ?? "" : "").ToArray() ?? [];
    }
    private void OnSpecificationDragOver(object? sender, DragEventArgs e)
    {
        if (DroppedFiles(e) is not { } paths || _viewModel is not { } model) return;
        _specificationDragGeneration++;
        e.Handled = true; e.DragEffects = DragDropEffects.None;
        if (!model.IsDraggingSpecification) model.SpecificationImportError = null;
        model.SpecificationDropError = model.IsImportingSpecification ? "Дождитесь завершения импорта." :
            OwnedWindows.Count > 0 ? "Закройте открытое окно." : model.ImportAvailabilityError ?? SpecificationImportService.CheckDropFiles(paths);
        model.SpecificationOverlayDescription = model.SpecificationDropError ?? "";
        model.IsDraggingSpecification = true;
        if (model.SpecificationDropError is null) e.DragEffects = DragDropEffects.Copy;
    }
    private void OnSpecificationDragLeave(object? sender, DragEventArgs e)
    {
        // Переход между дочерними элементами/появление оверлея тоже посылает Leave.
        // Следующий Enter/Over в том же цикле отменит очистку, без мерцания карточки.
        var generation = ++_specificationDragGeneration;
        Dispatcher.UIThread.Post(() =>
        {
            if (generation == _specificationDragGeneration && _viewModel is { } model) model.IsDraggingSpecification = false;
        }, DispatcherPriority.Background);
    }
    private async void OnSpecificationDrop(object? sender, DragEventArgs e)
    {
        if (DroppedFiles(e) is not { } paths) return;
        _specificationDragGeneration++;
        e.Handled = true; e.DragEffects = DragDropEffects.None;
        await ImportSpecificationsAsync(paths);
    }
    public async Task ImportSpecificationsAsync(IReadOnlyList<string> paths)
    {
        if (_viewModel is not { } model) return;
        model.IsDraggingSpecification = false;
        if (!IsVisible || model.IsImportingSpecification || OwnedWindows.Count > 0) return;
        _focusBeforeSpecification = FocusManager?.GetFocusedElement();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_updateChecksLifetime.Token);
        _specificationImportCancellation = cancellation;
        try
        {
            var form = await model.ReadSpecificationAsync(paths, SpecificationImporter, cancellation.Token);
            if (form is not null && IsVisible && !cancellation.IsCancellationRequested && OwnedWindows.Count == 0)
                await ShowNewOrderAsync(form);
        }
        finally { if (ReferenceEquals(_specificationImportCancellation, cancellation)) _specificationImportCancellation = null; }
    }
    private void OnCancelSpecificationImport(object? sender, RoutedEventArgs e) => _specificationImportCancellation?.Cancel();
    private void UpdateSpecificationFocus()
    {
        if (_viewModel?.IsImportingSpecification == true)
            Dispatcher.UIThread.Post(() =>
            { if (IsVisible && _viewModel?.IsImportingSpecification == true) CancelSpecificationImportButton.Focus(); });
        else if (_viewModel?.HasSpecificationImportError == true)
            Dispatcher.UIThread.Post(() =>
            { if (IsVisible && _viewModel?.HasSpecificationImportError == true && !_viewModel.IsDraggingSpecification) DismissSpecificationImportErrorButton.Focus(); });
        else
        {
            var focus = _focusBeforeSpecification; _focusBeforeSpecification = null;
            Dispatcher.UIThread.Post(() =>
            { if (IsVisible && _viewModel?.IsImportingSpecification != true && OwnedWindows.Count == 0) focus?.Focus(); });
        }
    }
}
