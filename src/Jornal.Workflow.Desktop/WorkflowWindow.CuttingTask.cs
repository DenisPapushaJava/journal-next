using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using Jornal.Core.Results;
using Jornal.Desktop.Clipboard;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowWindow
{
    private void OnCuttingTaskTextKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || ToggleWorkflowCuttingTaskPreview.Flyout is not { IsOpen: true } popup) return;
        popup.Hide();
        if (ToggleWorkflowCuttingTaskPreview.IsEffectivelyVisible && ToggleWorkflowCuttingTaskPreview.IsEffectivelyEnabled) ToggleWorkflowCuttingTaskPreview.Focus();
        e.Handled = true;
    }

    private async void OnPrepareShopTransfer(object? sender, RoutedEventArgs e)
        => await PrepareShopTransferAsync(false);
    private async void OnReviewShopTransfer(object? sender, RoutedEventArgs e)
        => await PrepareShopTransferAsync(true);
    private async Task PrepareShopTransferAsync(bool reviewOnly)
    {
        if (Model is not { } model || OwnedWindows.Count > 0 || !(reviewOnly ? model.CanCopyCuttingTask : model.CanSendToWork)) return;
        try
        {
            if (model.HasCuttingTaskText && string.IsNullOrWhiteSpace(model.CuttingTaskText))
            {
                ToggleWorkflowCuttingTaskPreview.Flyout?.ShowAt(ToggleWorkflowCuttingTaskPreview);
                WorkflowCuttingTaskPreview.Focus(); return;
            }
            if (model.ShopRoot is null)
            {
                // Show missing requisites before asking for a destination folder.
                model.CreateShopTransfer(WriteCuttingTaskClipboardAsync);
                if (!model.HasCuttingTaskText) return;
                await ShowSettingsAsync(1);
                if (!IsVisible || model.ShopRoot is null) return;
            }
            var preview = await model.PrepareShopTransferAsync(WriteCuttingTaskClipboardAsync, reviewOnly);
            if (!IsVisible || preview is null || _lifetime.IsCancellationRequested) return;
            if (reviewOnly || !preview.AllPrepared && !preview.WasCancelled && preview.Error is null)
                await ShowShopTransferDetailsAsync(preview);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            model.Error = ex is WorkflowValidationException ? ex.Message : "Не удалось открыть подготовку задания. Проверьте папку цеха в настройках.";
            Jornal.Infrastructure.Diagnostics.AppDiagnostics.Capture(Jornal.Infrastructure.Diagnostics.DiagnosticOperation.Unexpected, ex);
        }
    }
    private Task ShowShopTransferDetailsAsync(WorkflowShopTransferViewModel preview) =>
        new WorkflowShopTransferWindow(false) { DataContext = preview, RequestedThemeVariant = ActualThemeVariant }.ShowDialog(this);
    private async void OnShopTransferDetails(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanUse: true, ShopTransfer: { IsBusy: false } preview } || OwnedWindows.Count > 0) return;
        await ShowShopTransferDetailsAsync(preview);
    }
    private void OnCancelShopTransfer(object? sender, RoutedEventArgs e) => Model?.ShopTransfer?.Cancel();
    private async void OnRetryShopTransferText(object? sender, RoutedEventArgs e)
    { if (Model is { } model) await model.RetryShopTransferTextAsync(); }
    private async void OnOpenShopFolder(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Model is not { CanUse: true } model || sender is not Avalonia.Controls.Control { DataContext: WorkflowProjectRow row }) return;
        var path = await model.ShopFolderToOpenAsync(row, _lifetime.Token);
        if (path is null || !IsVisible || _lifetime.IsCancellationRequested) return;
        try
        {
            if (!await OpenFolderAsync(path) && IsVisible && !_lifetime.IsCancellationRequested && model.Projects.Contains(row))
                model.Error = "Папка задания в цехе не найдена или недоступна. Если задание ещё не готовили, нажмите «Отправить в работу…».";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (IsVisible && !_lifetime.IsCancellationRequested && model.Projects.Contains(row)) model.Error = "Не удалось открыть папку задания в цехе. Проверьте доступ к папке."; }
    }
    private async void OnCopyCuttingTask(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanCopyCuttingTask: true } model || OwnedWindows.Count > 0) return;
        await model.CopyCuttingTaskAsync(WriteCuttingTaskClipboardAsync);
    }
    protected virtual Task WriteCuttingTaskClipboardAsync(string text) =>
        new ResultClipboard(() => Clipboard).WriteAsync(ResultClipboardContent.FromText(text));

    private void OnGoToCuttingTaskProblem(object? sender, RoutedEventArgs e)
    {
        ToggleWorkflowCuttingTaskPreview.Flyout?.Hide();
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
