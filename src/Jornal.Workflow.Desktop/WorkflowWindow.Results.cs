using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Jornal.Core.Results;
using Jornal.Desktop.Clipboard;
using Jornal.Desktop.ViewModels;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowWindow
{
    private readonly TimeProvider _resultFeedbackClock;
    private WorkflowViewModel? _resultFeedbackModel;
    private ITimer? _resultFeedbackTimer;
    private long _resultFeedbackSchedule;
    private bool _resultFeedbackStopped;

    private void InitializeResultFeedback()
    { DataContextChanged += OnResultFeedbackModelChanged; AttachResultFeedbackModel(); }

    private void OnResultFeedbackModelChanged(object? sender, EventArgs e) => AttachResultFeedbackModel();
    private void AttachResultFeedbackModel()
    {
        if (_resultFeedbackModel is { } previous) previous.PropertyChanged -= OnResultFeedbackChanged;
        _resultFeedbackModel = null; CancelResultFeedbackExpiration();
        if (_resultFeedbackStopped) return;
        _resultFeedbackModel = Model;
        if (_resultFeedbackModel is { } model) model.PropertyChanged += OnResultFeedbackChanged;
        ScheduleResultFeedbackExpiration();
    }

    private void OnResultFeedbackChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(WorkflowViewModel.ResultFeedbackVersion)) ScheduleResultFeedbackExpiration(); }

    private void ScheduleResultFeedbackExpiration()
    {
        CancelResultFeedbackExpiration();
        if (_resultFeedbackStopped || !IsVisible || _resultFeedbackModel is not { HasTransientResultFeedback: true } model) return;
        var schedule = _resultFeedbackSchedule; var notice = model.ResultFeedbackVersion;
        _resultFeedbackTimer = _resultFeedbackClock.CreateTimer(_ => Dispatcher.UIThread.Post(() =>
        {
            // A queued callback must not dismiss a newer notice or another card's feedback.
            if (_resultFeedbackStopped || schedule != _resultFeedbackSchedule || !ReferenceEquals(Model, model) || notice != model.ResultFeedbackVersion) return;
            CancelResultFeedbackExpiration(); model.DismissResultFeedback(notice);
        }), null, TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
    }

    private void CancelResultFeedbackExpiration()
    { _resultFeedbackSchedule++; _resultFeedbackTimer?.Dispose(); _resultFeedbackTimer = null; }

    private void StopResultFeedback()
    { _resultFeedbackStopped = true; DataContextChanged -= OnResultFeedbackModelChanged; AttachResultFeedbackModel(); }

    private async void OnGenerateResult(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanGenerateResult: true } model || OwnedWindows.Count > 0) return;
        try
        {
            if (model.PrepareResultGeneration() is not { } draft) return;
            if (draft.NeedsConfirmation && !await new WorkflowResultReplaceWindow { RequestedThemeVariant = ActualThemeVariant }.ShowDialog<bool>(this)) return;
            if (IsVisible && ReferenceEquals(Model, model) && !_lifetime.IsCancellationRequested) model.ApplyResultGeneration(draft);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            model.SetResultFeedback("Не удалось сформировать результат. Прежний текст сохранён.", true);
            Jornal.Infrastructure.Diagnostics.AppDiagnostics.Capture(Jornal.Infrastructure.Diagnostics.DiagnosticOperation.Unexpected, ex);
        }
    }

    protected virtual Task WriteResultClipboardAsync(ResultClipboardContent content) => new ResultClipboard(() => Clipboard).WriteAsync(content);
    private sealed class WindowResultClipboard(Func<ResultClipboardContent, Task> write) : IResultClipboard
    { public Task WriteAsync(ResultClipboardContent content) => write(content); }

    private void OnChooseResultColor(object? sender, RoutedEventArgs e)
    {
        if (Model is { CanEditResult: true } model && sender is Control { DataContext: ResultColorOption color }) model.ResultTextColor = color.Hex;
        WorkflowResultColor.Flyout?.Hide(); WorkflowResultColor.Focus();
    }
    private void OnDefaultResultColor(object? sender, RoutedEventArgs e)
    { if (Model is { CanEditResult: true } model) model.ResultTextColor = ResultTextStyle.DefaultColor; WorkflowResultColor.Flyout?.Hide(); WorkflowResultColor.Focus(); }

    private void OnResultMenuOpened(object? sender, EventArgs e)
    {
        ResultMenuUndo.IsEnabled = WorkflowResultEditor.CanUndo && Model?.CanEditResult == true;
        ResultMenuCut.IsEnabled = WorkflowResultEditor.CanCut && Model?.CanEditResult == true;
        ResultMenuCopySelection.IsEnabled = WorkflowResultEditor.CanCopy;
        ResultMenuPaste.IsEnabled = WorkflowResultEditor.CanPaste && Model?.CanEditResult == true;
        ResultMenuSelectAll.IsEnabled = WorkflowResultEditor.Text?.Length > 0;
    }
    private void OnResultUndo(object? sender, RoutedEventArgs e) => WorkflowResultEditor.Undo();
    private void OnResultCut(object? sender, RoutedEventArgs e) => WorkflowResultEditor.Cut();
    private void OnResultCopySelection(object? sender, RoutedEventArgs e) => WorkflowResultEditor.Copy();
    private void OnResultPaste(object? sender, RoutedEventArgs e) => WorkflowResultEditor.Paste();
    private void OnResultSelectAll(object? sender, RoutedEventArgs e) => WorkflowResultEditor.SelectAll();
}
