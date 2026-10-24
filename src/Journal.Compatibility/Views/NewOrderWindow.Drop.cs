using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jornal.Desktop.Controls;
namespace Jornal.Desktop.Views;
public partial class NewOrderWindow
{
    private int _fileDragGeneration;
    private void ConfigureFileDrop()
    {
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnFileDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragOverEvent, OnFileDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, OnFileDragLeave, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnFileDrop, RoutingStrategies.Bubble, handledEventsToo: true);
    }
    private static IReadOnlyList<string>? FormDroppedFiles(DragEventArgs e) => !e.DataTransfer.Contains(DataFormat.File) ? null :
        e.DataTransfer.TryGetFiles()?.Select(file => file is IStorageFile ? file.TryGetLocalPath() ?? "" : "").ToArray() ?? [];
    private void OnFileDragOver(object? sender, DragEventArgs e)
    {
        if (_viewModel is not { } model || FormDroppedFiles(e) is not { } paths) return;
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
        if (FormDroppedFiles(e) is not { } paths) return;
        _fileDragGeneration++; e.Handled = true; e.DragEffects = DragDropEffects.None;
        var model = _viewModel;
        var accepted = IsVisible && OwnedWindows.Count == 0 && model is not null && model.CheckFileDrop(paths) is null;
        if (accepted) Activate();
        var cancellation = _importLifetime.Token;
        await ImportSpecificationsAsync(paths);
        if (!accepted || model is null) return;
        // Import temporarily disables the single form (or hides it when switching to a batch).
        // Restore input only after bindings settle, without taking focus from another control/window.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (cancellation.IsCancellationRequested || !IsVisible || !IsActive || WindowState == WindowState.Minimized ||
                OwnedWindows.Count != 0 || !ReferenceEquals(model, _viewModel) || model.IsReadingSpecifications ||
                model.HasPendingImport || model.HasImportError ||
                !(model.IsBatchMode ? model.BatchItems.Any(item => item.HasParameters) : model.HasImport)) return;
            var manager = this.GetVisualDescendants().OfType<OrderEntryControl>().FirstOrDefault(control =>
                control.IsEffectivelyVisible && control.IsEffectivelyEnabled && control.DataContext == model.Manager);
            if (manager is null || manager.IsKeyboardFocusWithin) return;
            if (FocusManager?.GetFocusedElement() is Control focused && focused != this &&
                focused.IsEffectivelyVisible && focused.IsEffectivelyEnabled) return;
            manager.FocusInput();
        }, DispatcherPriority.Background);
    }
    public async Task ImportSpecificationsAsync(IReadOnlyList<string> paths)
    {
        if (_viewModel is not { } model) return;
        model.IsDraggingFiles = false;
        if (!IsVisible) return;
        if (model.CheckFileDrop(paths) is { } error) { model.ImportError = error; return; }
        await model.ImportFilesAsync(paths, _importLifetime.Token, appendToCurrent: true);
    }
}
