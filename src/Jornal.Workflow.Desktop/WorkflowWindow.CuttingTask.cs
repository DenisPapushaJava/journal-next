using Avalonia.Interactivity;
using Avalonia.Threading;
using Jornal.Core.Results;
using Jornal.Desktop.Clipboard;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowWindow
{
    private async void OnCopyCuttingTask(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanCopyCuttingTask: true } model || OwnedWindows.Count > 0) return;
        await model.CopyCuttingTaskAsync(WriteCuttingTaskClipboardAsync);
    }
    protected virtual Task WriteCuttingTaskClipboardAsync(string text) =>
        new ResultClipboard(() => Clipboard).WriteAsync(ResultClipboardContent.FromText(text));

    private void OnGoToCuttingTaskProblem(object? sender, RoutedEventArgs e)
    {
        switch (Model?.GoToCuttingTaskProblem())
        {
            case CuttingTaskTarget.Header:
                OrderHeaderExpander.IsExpanded = true;
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsVisible) return;
                    if (string.IsNullOrWhiteSpace(Model?.Blcb)) WorkflowBlcbInput.Focus(); else WorkflowManagerInput.Focus();
                }, DispatcherPriority.Loaded);
                break;
            case CuttingTaskTarget.Project: OnGoToProject(sender, e); break;
            case CuttingTaskTarget.Projects:
                if (Model?.IsPlanning == true) WorkflowProjectThickness.Focus(); else PrepareWorkflowProject.Focus();
                break;
            case CuttingTaskTarget.Refresh:
                if (DiscardWorkflowCardChanges.IsEffectivelyVisible) DiscardWorkflowCardChanges.Focus();
                else Model?.ReloadCommand.Execute(null);
                break;
            case CuttingTaskTarget.Reports: OnRefreshReports(sender, e); break;
        }
    }

    private async void OnOpenBendingFolder(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Model is not { CanUse: true } model) return;
        var selected = model.SelectedDraft?.Id;
        var path = await model.PrepareSelectedBendingFolderAsync(_lifetime.Token);
        if (path is null || !IsVisible || _lifetime.IsCancellationRequested || model.SelectedDraft?.Id != selected) return;
        try
        {
            if (!await OpenFolderAsync(path) && IsVisible && !_lifetime.IsCancellationRequested)
                model.Error = "Папка ГИБКА подготовлена, но открыть её в проводнике не удалось.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (IsVisible && !_lifetime.IsCancellationRequested) model.Error = "Папка ГИБКА подготовлена, но открыть её в проводнике не удалось."; }
    }
}
