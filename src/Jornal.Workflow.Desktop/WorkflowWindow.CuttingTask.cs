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
        if (Model is not { } model || OwnedWindows.Count > 0 || !(reviewOnly ? model.CanCopyCuttingTask : model.CanRequestSendToWork)) return;
        try
        {
            if (!reviewOnly && !model.CanSendToWork)
            {
                model.ExplainSendToWork();
                if (model.HasCuttingTaskText && (model.IsCuttingTaskTextStale || string.IsNullOrWhiteSpace(model.CuttingTaskText)))
                    ToggleWorkflowCuttingTaskPreview.Flyout?.ShowAt(ToggleWorkflowCuttingTaskPreview);
                return;
            }
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
            var operation = model.PrepareShopTransferAsync(WriteCuttingTaskClipboardAsync, reviewOnly);
            var preview = reviewOnly ? await operation : await WithShopProgressAsync(operation);
            if (!IsVisible || preview is null || _lifetime.IsCancellationRequested) return;
            if (preview.CanActOnOfficeReview)
                await ContinueOfficeShopAsync(model, preview, reviewOnly);
            else
                await ShowRemainingShopReviewAsync(preview, reviewOnly);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            model.Error = ex is WorkflowValidationException ? ex.Message : "Не удалось открыть подготовку задания. Проверьте папку цеха в настройках.";
            Jornal.Infrastructure.Diagnostics.AppDiagnostics.Capture(Jornal.Infrastructure.Diagnostics.DiagnosticOperation.Unexpected, ex);
        }
    }

    private Task ShowRemainingShopReviewAsync(WorkflowShopTransferViewModel preview, bool reviewOnly) =>
        IsVisible && !_lifetime.IsCancellationRequested && !preview.CanActOnOfficeReview &&
        (reviewOnly || !preview.AllPrepared && !preview.WasCancelled && preview.Error is null)
            ? ShowShopTransferDetailsAsync(preview) : Task.CompletedTask;

    private async void OnContinueOfficeShop(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanUse: true, ShopTransfer: { CanActOnOfficeReview: true } preview } model || OwnedWindows.Count > 0) return;
        await ContinueOfficeShopAsync(model, preview, preview.ReviewOnly);
    }

    private async Task ContinueOfficeShopAsync(WorkflowViewModel model, WorkflowShopTransferViewModel preview, bool reviewOnly)
    {
        if (!IsVisible || !preview.CanActOnOfficeReview || preview.OfficeReview is not { } request) return;
        bool Current() => IsVisible && !_lifetime.IsCancellationRequested && ReferenceEquals(Model, model) &&
            ReferenceEquals(model.ShopTransfer, preview) && ReferenceEquals(preview.OfficeReview, request);
        if (!Current()) return;
        try
        {
            if (request.Conflict is { } conflict)
            {
                var row = model.Projects.SingleOrDefault(project => project.Project.Id == conflict.ProjectId);
                if (row is null || !await ResolveOfficeWorkingConflictAsync(model, row, conflict.ToConflict(), Current, forShopTransfer: true)) return;
            }
            else
            {
                var files = string.Join("\n\n", request.Files.GroupBy(file => file.ProjectId)
                    .Select(group => group.First().ProjectName + "\n" + string.Join("\n", group.Select(file => file.Path))));
                var prompt = new WorkflowProjectActionWindow("Передача файлов в офис",
                    "Эти файлы будут сохранены в офисных папках проектов перед подготовкой задания в цех.",
                    files, "Отправить файлы и продолжить") { RequestedThemeVariant = ActualThemeVariant };
                if (!await prompt.ShowDialog<bool>(this)) return;
            }
            if (!Current()) return;
            var result = await WithShopProgressAsync(model.ContinueOfficeShopTransferAsync(preview, request, reviewOnly));
            if (result is null || !IsVisible || _lifetime.IsCancellationRequested) return;
            if (request.Conflict is not null && result.OfficeReview is { Conflict: null } && result.CanActOnOfficeReview)
                await ContinueOfficeShopAsync(model, result, reviewOnly);
            else
                await ShowRemainingShopReviewAsync(result, reviewOnly);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (IsVisible && ReferenceEquals(model.ShopTransfer, preview))
                preview.Error = ex is WorkflowValidationException ? ex.Message : "Не удалось продолжить передачу. Проверьте подключение и повторите действие.";
            Jornal.Infrastructure.Diagnostics.AppDiagnostics.Capture(Jornal.Infrastructure.Diagnostics.DiagnosticOperation.Unexpected, ex);
        }
        finally
        {
            if (IsVisible && ReferenceEquals(Model, model) && !model.IsBusy && !_lifetime.IsCancellationRequested)
                _ = model.RefreshOfficePendingFilesAsync(_lifetime.Token, recentlyOpenedOnly: true);
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
            case CuttingTaskTarget.Reports: OnCalculateOrder(sender, e); break;
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
